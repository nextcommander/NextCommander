namespace NextCommander.Plugin.Packages.Formats;

/// <summary>
/// The LZX decompressor of Microsoft Cabinets (window 2^15 to 2^21, as in MS-PATCH / the cabinet format): verbatim,
/// aligned offset and uncompressed blocks, the three repeated offsets, Huffman trees sent as deltas through a pretree,
/// and the "E8" call translation of x86 code. The input is the folder's data blocks one after the other; the output
/// comes in 32 KB frames, after each of which the bit stream is aligned to 16 bits again.
/// </summary>
internal sealed class LzxDecoder
{
    private const int FrameSize = 32768;
    private const int MainTreeMax = 256 + (50 * 8);
    private const int LengthTreeSize = 249;

    private static readonly int[] s_extraBits = new int[52];
    private static readonly int[] s_positionBase = new int[52];

    private readonly Stream _input;
    private readonly byte[] _window;
    private readonly int _mask;
    private readonly int _mainElements;
    private readonly byte[] _mainLengths = new byte[MainTreeMax];
    private readonly byte[] _lengthLengths = new byte[LengthTreeSize];
    private readonly byte[] _alignedLengths = new byte[8];
    private readonly Huffman _main = new();
    private readonly Huffman _length = new();
    private readonly Huffman _aligned = new();
    private readonly Huffman _pretree = new();

    private uint _bitBuffer;
    private int _bitsLeft;
    private readonly byte[] _pushedBack = new byte[2];
    private int _pushedBackCount;
    private int _last1 = -1, _last2 = -1;

    private readonly byte[] _inputBuffer = new byte[64 * 1024];
    private int _inputOffset;
    private int _inputLength;

    private int _position;
    private int _frameStart;
    private int _carry;
    private long _outputDone;
    private int _frame;
    private bool _headerRead;
    private int _intelFileSize;
    private bool _intelStarted;
    private int _blockType;
    private int _blockLength;
    private int _blockRemaining;
    private int _r0 = 1, _r1 = 1, _r2 = 1;

    static LzxDecoder()
    {
        for (int i = 0, j = 0; i < 52; i += 2)
        {
            s_extraBits[i] = j;
            s_extraBits[i + 1] = j;
            if (i != 0 && j < 17)
            {
                j++;
            }
        }
        for (int i = 0, j = 0; i < 52; i++)
        {
            s_positionBase[i] = j;
            j += 1 << s_extraBits[i];
        }
    }

    /// <param name="input">The compressed bytes of the folder (its data blocks one after the other).</param>
    /// <param name="windowBits">15 to 21, from the folder's compression type.</param>
    public LzxDecoder(Stream input, int windowBits)
    {
        if (windowBits is < 15 or > 21)
        {
            throw new InvalidDataException($"An LZX window of 2^{windowBits} bytes is not possible.");
        }
        _input = input;
        _window = new byte[1 << windowBits];
        _mask = _window.Length - 1;
        int[] slots = [30, 32, 34, 36, 38, 42, 50];
        _mainElements = 256 + (slots[windowBits - 15] * 8);
    }

    /// <summary>Unpacks the next frame (<paramref name="size"/> bytes: 32 KB, or what is left at the end).</summary>
    public byte[] NextFrame(int size)
    {
        var start = _frameStart;
        var produced = _carry;
        while (produced < size)
        {
            if (_blockRemaining <= 0)
            {
                ReadBlockHeader();
            }
            if (_blockType == 3)
            {
                var count = Math.Min(_blockRemaining, size - produced);
                for (var i = 0; i < count; i++)
                {
                    _window[_position] = ReadByte();
                    _position = (_position + 1) & _mask;
                }
                produced += count;
                _blockRemaining -= count;
                continue;
            }
            var written = DecodeSymbol();
            produced += written;
            _blockRemaining -= written;
        }
        // the frame ends on a 16-bit boundary of the input
        if (_blockType != 3 && _bitsLeft > 0)
        {
            Ensure(16);
            Remove(_bitsLeft & 15);
        }
        var frame = new byte[size];
        for (var i = 0; i < size; i++)
        {
            frame[i] = _window[(start + i) & _mask];
        }
        // a match that ran over the end of the frame is already in the window: it starts the next one
        _carry = produced - size;
        _frameStart = (start + size) & _mask;
        Translate(frame);
        _outputDone += size;
        _frame++;
        return frame;
    }

    /// <summary>Undoes the encoder's change of x86 CALL targets (E8 + absolute offset) in the first 32768 frames.</summary>
    private void Translate(byte[] frame)
    {
        if (!_intelStarted || _intelFileSize == 0 || _frame >= 32768 || frame.Length <= 10)
        {
            return;
        }
        var current = (int)_outputDone;
        var end = frame.Length - 10;
        for (var i = 0; i < end;)
        {
            if (frame[i++] != 0xE8)
            {
                current++;
                continue;
            }
            var absolute = frame[i] | (frame[i + 1] << 8) | (frame[i + 2] << 16) | (frame[i + 3] << 24);
            if (absolute >= -current && absolute < _intelFileSize)
            {
                var relative = absolute >= 0 ? absolute - current : absolute + _intelFileSize;
                frame[i] = (byte)relative;
                frame[i + 1] = (byte)(relative >> 8);
                frame[i + 2] = (byte)(relative >> 16);
                frame[i + 3] = (byte)(relative >> 24);
            }
            i += 4;
            current += 5;
        }
    }

    private void ReadBlockHeader()
    {
        if (_blockType == 3)
        {
            if ((_blockLength & 1) != 0)
            {
                ReadByte(); // an uncompressed block of odd length is padded to 16 bits
            }
            _bitBuffer = 0;
            _bitsLeft = 0;
        }
        if (!_headerRead)
        {
            _headerRead = true;
            if (ReadBits(1) == 1)
            {
                var high = ReadBits(16);
                var low = ReadBits(16);
                _intelFileSize = (int)((high << 16) | low);
            }
        }
        _blockType = (int)ReadBits(3);
        var length = (ReadBits(16) << 8) | ReadBits(8);
        _blockLength = _blockRemaining = (int)length;
        switch (_blockType)
        {
            case 2:
                for (var i = 0; i < 8; i++)
                {
                    _alignedLengths[i] = (byte)ReadBits(3);
                }
                _aligned.Build(_alignedLengths, 8);
                ReadMainAndLength();
                break;
            case 1:
                ReadMainAndLength();
                break;
            case 3:
                _intelStarted = true;
                // to a 16-bit boundary (1 to 16 bits of padding), then R0, R1, R2 and the bytes as they are
                Ensure(16);
                if (_bitsLeft > 16)
                {
                    Unread();
                }
                _bitBuffer = 0;
                _bitsLeft = 0;
                _r0 = ReadInt32();
                _r1 = ReadInt32();
                _r2 = ReadInt32();
                break;
            default:
                throw new InvalidDataException("A damaged LZX block (unknown block type).");
        }
        if (_blockLength == 0)
        {
            throw new InvalidDataException("A damaged LZX block (empty).");
        }
    }

    private void ReadMainAndLength()
    {
        ReadLengths(_mainLengths, 0, 256);
        ReadLengths(_mainLengths, 256, _mainElements);
        _main.Build(_mainLengths, _mainElements);
        if (_mainLengths[0xE8] != 0)
        {
            _intelStarted = true;
        }
        ReadLengths(_lengthLengths, 0, LengthTreeSize);
        _length.Build(_lengthLengths, LengthTreeSize);
    }

    /// <summary>Code lengths sent as changes to the previous ones, through a pretree of 20 symbols.</summary>
    private void ReadLengths(byte[] lengths, int first, int last)
    {
        var pretree = new byte[20];
        for (var i = 0; i < 20; i++)
        {
            pretree[i] = (byte)ReadBits(4);
        }
        _pretree.Build(pretree, 20);
        for (var x = first; x < last;)
        {
            var z = _pretree.Decode(this);
            switch (z)
            {
                case 17:
                    for (var y = (int)ReadBits(4) + 4; y > 0 && x < last; y--)
                    {
                        lengths[x++] = 0;
                    }
                    break;
                case 18:
                    for (var y = (int)ReadBits(5) + 20; y > 0 && x < last; y--)
                    {
                        lengths[x++] = 0;
                    }
                    break;
                case 19:
                    var run = (int)ReadBits(1) + 4;
                    var value = lengths[x] - _pretree.Decode(this);
                    if (value < 0)
                    {
                        value += 17;
                    }
                    for (; run > 0 && x < last; run--)
                    {
                        lengths[x++] = (byte)value;
                    }
                    break;
                default:
                    var length = lengths[x] - z;
                    lengths[x++] = (byte)(length < 0 ? length + 17 : length);
                    break;
            }
        }
    }

    /// <summary>One literal or match of a verbatim or aligned offset block; returns the bytes written.</summary>
    private int DecodeSymbol()
    {
        var element = _main.Decode(this);
        if (element < 256)
        {
            _window[_position] = (byte)element;
            _position = (_position + 1) & _mask;
            return 1;
        }
        element -= 256;
        var length = element & 7;
        if (length == 7)
        {
            length += _length.Decode(this);
        }
        length += 2;
        var slot = element >> 3;
        int offset;
        if (slot > 2)
        {
            var extra = s_extraBits[slot];
            if (_blockType == 2)
            {
                offset = s_positionBase[slot] - 2;
                if (extra > 3)
                {
                    offset += (int)ReadBits(extra - 3) << 3;
                    offset += _aligned.Decode(this);
                }
                else if (extra == 3)
                {
                    offset += _aligned.Decode(this);
                }
                else if (extra > 0)
                {
                    offset += (int)ReadBits(extra);
                }
                else
                {
                    offset = 1;
                }
            }
            else
            {
                offset = slot != 3 ? s_positionBase[slot] - 2 + (int)ReadBits(extra) : 1;
            }
            _r2 = _r1;
            _r1 = _r0;
            _r0 = offset;
        }
        else if (slot == 0)
        {
            offset = _r0;
        }
        else if (slot == 1)
        {
            offset = _r1;
            _r1 = _r0;
            _r0 = offset;
        }
        else
        {
            offset = _r2;
            _r2 = _r0;
            _r0 = offset;
        }
        if (offset <= 0 || offset > _window.Length)
        {
            throw new InvalidDataException("A damaged LZX block (a match outside the window).");
        }
        var from = (_position - offset) & _mask;
        for (var i = 0; i < length; i++)
        {
            _window[_position] = _window[from];
            _position = (_position + 1) & _mask;
            from = (from + 1) & _mask;
        }
        return length;
    }

    // ---------------------------------------------------------------- bits: 16-bit little-endian words, high bit first

    private byte ReadByte()
    {
        if (_pushedBackCount > 0)
        {
            return _pushedBack[--_pushedBackCount];
        }
        if (_inputOffset == _inputLength)
        {
            _inputLength = _input.Read(_inputBuffer);
            _inputOffset = 0;
        }
        // past the end: zeros, as the encoder padded
        int b = _inputOffset < _inputLength ? _inputBuffer[_inputOffset++] : 0;
        _last2 = _last1;
        _last1 = b;
        return (byte)b;
    }

    /// <summary>Gives the last two bytes back to the input.</summary>
    private void Unread()
    {
        _pushedBack[0] = (byte)_last1;
        _pushedBack[1] = (byte)_last2;
        _pushedBackCount = 2;
    }

    private int ReadInt32() => ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24);

    private void Ensure(int count)
    {
        while (_bitsLeft < count)
        {
            var low = ReadByte();
            var high = ReadByte();
            _bitBuffer |= (uint)((high << 8) | low) << (16 - _bitsLeft);
            _bitsLeft += 16;
        }
    }

    /// <summary>The next 16 bits without taking them.</summary>
    internal uint Peek16()
    {
        Ensure(16);
        return _bitBuffer >> 16;
    }

    internal void Remove(int count)
    {
        _bitBuffer <<= count;
        _bitsLeft -= count;
    }

    private uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }
        Ensure(count);
        var value = _bitBuffer >> (32 - count);
        Remove(count);
        return value;
    }

    /// <summary>A canonical Huffman code (codes up to 16 bits), decoded through a 10-bit table and bit by bit beyond it.</summary>
    private sealed class Huffman
    {
        private const int TableBits = 10;

        private readonly int[] _counts = new int[17];
        private int[] _symbols = [];
        private readonly int[] _table = new int[1 << TableBits]; // symbol << 5 | length; 0: longer code (or none)

        public void Build(byte[] lengths, int count)
        {
            Array.Clear(_counts);
            for (var i = 0; i < count; i++)
            {
                _counts[lengths[i]]++;
            }
            _counts[0] = 0;
            var offsets = new int[18];
            for (var len = 1; len <= 16; len++)
            {
                offsets[len + 1] = offsets[len] + _counts[len];
            }
            _symbols = new int[offsets[17]];
            for (var i = 0; i < count; i++)
            {
                if (lengths[i] != 0)
                {
                    _symbols[offsets[lengths[i]]++] = i;
                }
            }
            // the short codes in the table: every index that starts with the code
            Array.Clear(_table);
            var code = 0;
            var index = 0;
            for (var len = 1; len <= TableBits; len++)
            {
                for (var n = 0; n < _counts[len]; n++)
                {
                    var symbol = _symbols[index++];
                    var first = code << (TableBits - len);
                    var span = 1 << (TableBits - len);
                    if (first + span > _table.Length)
                    {
                        throw new InvalidDataException("A damaged LZX block (bad Huffman code lengths).");
                    }
                    for (var k = 0; k < span; k++)
                    {
                        _table[first + k] = (symbol << 5) | len;
                    }
                    code++;
                }
                code <<= 1;
            }
        }

        public int Decode(LzxDecoder bits)
        {
            var peek = bits.Peek16();
            var entry = _table[peek >> (16 - TableBits)];
            if (entry != 0)
            {
                bits.Remove(entry & 31);
                return entry >> 5;
            }
            // longer codes: canonical decoding, one bit at a time
            int code = 0, first = 0, index = 0;
            for (var len = 1; len <= 16; len++)
            {
                code |= (int)(peek >> (16 - len)) & 1;
                var count = _counts[len];
                if (code - first < count)
                {
                    bits.Remove(len);
                    return _symbols[index + code - first];
                }
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new InvalidDataException("A damaged LZX block (a code that is not in its Huffman table).");
        }
    }
}

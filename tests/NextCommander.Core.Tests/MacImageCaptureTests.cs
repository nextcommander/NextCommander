using NextCommander.Platform.Mtp.LibMtp;

namespace NextCommander.Core.Tests;

/// <summary>macOS: a phone the camera service holds is opened again after the service was stopped.</summary>
public sealed class MacImageCaptureTests
{
    [Fact]
    public void A_device_held_by_the_camera_service_opens_after_it_is_stopped()
    {
        // the service holds it until it is stopped twice (it comes back once, as after the phone connects again)
        var held = 2;
        var opens = 0;
        var waits = 0;
        var device = MacImageCapture.OpenReleasing(
            () =>
            {
                opens++;
                return held > 0 ? IntPtr.Zero : 42;
            },
            () =>
            {
                held--;
                return true;
            },
            _ => waits++);
        Assert.Equal(42, device);
        Assert.Equal((3, 2), (opens, waits));
    }

    [Fact]
    public void A_device_that_cannot_be_opened_for_another_reason_is_not_tried_for_long()
    {
        var opens = 0;
        var device = MacImageCapture.OpenReleasing(() =>
        {
            opens++;
            return IntPtr.Zero;
        }, () => false, _ => { });
        Assert.Equal(IntPtr.Zero, device);
        Assert.Equal(2, opens); // once more after a moment, then no service holds it: given up

        opens = 0;
        Assert.Equal(IntPtr.Zero, MacImageCapture.OpenReleasing(() =>
        {
            opens++;
            return IntPtr.Zero;
        }, () => true, _ => { }));
        Assert.Equal(1 + MacImageCapture.Attempts, opens); // the service keeps coming back: a few times only

        Assert.Equal(7, MacImageCapture.OpenReleasing(() => 7, () => throw new InvalidOperationException("not called"), _ => { }));
    }
}

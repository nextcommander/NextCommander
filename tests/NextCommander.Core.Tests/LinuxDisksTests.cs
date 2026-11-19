using NextCommander.Platform.Linux;

namespace NextCommander.Core.Tests;

/// <summary>Linux drive bar: the partitions of lsblk as the desktop's file manager shows them.</summary>
public sealed class LinuxDisksTests
{
    // An Ubuntu machine: NVMe system disk (EFI, root, /boot), a second SATA disk not mounted (NTFS with a label, ext4
    // without), a USB stick mounted by the desktop, a LUKS disk opened, snaps as loop devices, an empty card reader.
    private const string Ubuntu = """
        {
           "blockdevices": [
              {"path":"/dev/loop0", "name":"loop0", "label":null, "size":4096, "fstype":"squashfs", "mountpoints":["/snap/bare/5"], "type":"loop", "rm":false, "hotplug":false, "parttypename":null},
              {"path":"/dev/sda", "name":"sda", "label":null, "size":1000204886016, "fstype":null, "mountpoints":[null], "type":"disk", "rm":false, "hotplug":false, "parttypename":null,
                 "children": [
                    {"path":"/dev/sda1", "name":"sda1", "label":null, "size":16777216, "fstype":null, "mountpoints":[null], "type":"part", "rm":false, "hotplug":false, "parttypename":"Microsoft reserved"},
                    {"path":"/dev/sda2", "name":"sda2", "label":"Veri", "size":900000000000, "fstype":"ntfs", "mountpoints":[null], "type":"part", "rm":false, "hotplug":false, "parttypename":"Microsoft basic data"},
                    {"path":"/dev/sda3", "name":"sda3", "label":null, "size":99000000000, "fstype":"ext4", "mountpoints":[null], "type":"part", "rm":false, "hotplug":false, "parttypename":"Linux filesystem"}
                 ]
              },
              {"path":"/dev/sdb", "name":"sdb", "label":null, "size":32000000000, "fstype":null, "mountpoints":[null], "type":"disk", "rm":true, "hotplug":true, "parttypename":null,
                 "children": [
                    {"path":"/dev/sdb1", "name":"sdb1", "label":"USB", "size":32000000000, "fstype":"exfat", "mountpoints":["/media/ali/USB"], "type":"part", "rm":true, "hotplug":true, "parttypename":"Microsoft basic data"}
                 ]
              },
              {"path":"/dev/sdc", "name":"sdc", "label":null, "size":500107862016, "fstype":"crypto_LUKS", "mountpoints":[null], "type":"disk", "rm":false, "hotplug":false, "parttypename":null,
                 "children": [
                    {"path":"/dev/mapper/luks-1", "name":"luks-1", "label":"Yedek", "size":500091084800, "fstype":"btrfs", "mountpoints":["/mnt/yedek"], "type":"crypt", "rm":false, "hotplug":false, "parttypename":null}
                 ]
              },
              {"path":"/dev/sdd", "name":"sdd", "label":null, "size":0, "fstype":null, "mountpoints":[null], "type":"disk", "rm":true, "hotplug":true, "parttypename":null},
              {"path":"/dev/nvme0n1", "name":"nvme0n1", "label":null, "size":512110190592, "fstype":null, "mountpoints":[null], "type":"disk", "rm":false, "hotplug":false, "parttypename":null,
                 "children": [
                    {"path":"/dev/nvme0n1p1", "name":"nvme0n1p1", "label":null, "size":1127219200, "fstype":"vfat", "mountpoints":["/boot/efi"], "type":"part", "rm":false, "hotplug":false, "parttypename":"EFI System"},
                    {"path":"/dev/nvme0n1p2", "name":"nvme0n1p2", "label":null, "size":2147483648, "fstype":"ext4", "mountpoints":["/boot"], "type":"part", "rm":false, "hotplug":false, "parttypename":"Linux filesystem"},
                    {"path":"/dev/nvme0n1p3", "name":"nvme0n1p3", "label":null, "size":8589934592, "fstype":"swap", "mountpoints":["[SWAP]"], "type":"part", "rm":false, "hotplug":false, "parttypename":"Linux swap"},
                    {"path":"/dev/nvme0n1p4", "name":"nvme0n1p4", "label":null, "size":500000000000, "fstype":"ext4", "mountpoints":["/var/snap/firefox/common/host-hunspell", "/"], "type":"part", "rm":false, "hotplug":false, "parttypename":"Linux filesystem"}
                 ]
              }
           ]
        }
        """;

    [Fact]
    public void Second_disk_unmounted_partitions_and_mounted_ones_but_not_the_system()
    {
        var disks = LinuxDisks.Parse(Ubuntu);
        Assert.Equal(["/dev/sda2", "/dev/sda3", "/dev/sdb1", "/dev/mapper/luks-1"], disks.Select(d => d.Device));
        var data = disks[0];
        Assert.Equal(("Veri", null, false), (data.Name, data.MountPoint, data.Removable)); // the second disk, not mounted yet
        Assert.Equal(LinuxDisks.SizeText(99000000000) + " volume", disks[1].Name);        // no label: its size, like Nautilus
        Assert.Equal(("USB", "/media/ali/USB", true), (disks[2].Name, disks[2].MountPoint, disks[2].Removable));
        Assert.Equal(("Yedek", "/mnt/yedek"), (disks[3].Name, disks[3].MountPoint));      // what the LUKS container opens
    }

    [Fact]
    public void Older_lsblk_with_one_mount_point_and_text_flags()
    {
        const string old = """
            {"blockdevices": [
               {"name":"sda", "label":null, "size":"256060514304", "fstype":null, "mountpoint":null, "type":"disk", "rm":"0", "hotplug":"0",
                  "children": [
                     {"name":"sda1", "label":null, "size":"256059465728", "fstype":"ext4", "mountpoint":"/", "type":"part", "rm":"0", "hotplug":"0"}
                  ]},
               {"name":"sdb", "label":null, "size":"2000398934016", "fstype":null, "mountpoint":null, "type":"disk", "rm":"0", "hotplug":"1",
                  "children": [
                     {"name":"sdb1", "label":"Arşiv", "size":"2000397885440", "fstype":"ext4", "mountpoint":null, "type":"part", "rm":"0", "hotplug":"1"}
                  ]}
            ]}
            """;
        var disk = Assert.Single(LinuxDisks.Parse(old));
        Assert.Equal(("/dev/sdb1", "Arşiv", null, true), (disk.Device, disk.Name, disk.MountPoint, disk.Removable));
    }

    [Fact]
    public void A_disk_is_named_by_its_label_then_its_partition_name_then_its_mount_folder()
    {
        const string json = """
            {"blockdevices": [
               {"path":"/dev/sdb", "name":"sdb", "label":null, "partlabel":null, "size":2000398934016, "fstype":null, "mountpoints":[null], "type":"disk", "rm":false, "hotplug":true,
                  "children": [
                     {"path":"/dev/sdb1", "name":"sdb1", "label":"Yedekler", "partlabel":"Basic data partition", "size":1000000000000, "fstype":"ntfs", "mountpoints":["/mnt/disk1"], "type":"part", "rm":false, "hotplug":true},
                     {"path":"/dev/sdb2", "name":"sdb2", "label":null, "partlabel":"Fotoğraflar", "size":900000000000, "fstype":"exfat", "mountpoints":["/mnt/disk2"], "type":"part", "rm":false, "hotplug":true},
                     {"path":"/dev/sdb3", "name":"sdb3", "label":"", "partlabel":null, "size":50000000000, "fstype":"ext4", "mountpoints":["/mnt/disk3"], "type":"part", "rm":false, "hotplug":true}
                  ]}
            ]}
            """;
        // not the mount folder's name ("disk1") when the disk has a label
        Assert.Equal(["Yedekler", "Fotoğraflar", "disk3"], LinuxDisks.Parse(json).Select(d => d.Name));
    }

    [Fact]
    public void Default_partition_names_and_id_folders_are_not_names()
    {
        // Windows' NTFS data partition without a label, mounted by the desktop under its serial number
        const string json = """
            {"blockdevices": [
               {"path":"/dev/sdb", "name":"sdb", "label":null, "partlabel":null, "size":500107862016, "fstype":null, "mountpoints":[null], "type":"disk", "rm":false, "hotplug":false,
                  "children": [
                     {"path":"/dev/sdb2", "name":"sdb2", "label":null, "partlabel":"Basic data partition", "size":500000000000, "fstype":"ntfs", "mountpoints":["/run/media/ali/ACE84A32E849FB5A"], "type":"part", "rm":false, "hotplug":false},
                     {"path":"/dev/sdb3", "name":"sdb3", "label":null, "partlabel":"Linux filesystem", "size":7000000000, "fstype":"ext4", "mountpoints":["/run/media/ali/0f3c2b1a-4d5e-4f60-8a9b-0c1d2e3f4a5b"], "type":"part", "rm":false, "hotplug":false}
                  ]}
            ]}
            """;
        Assert.Equal([LinuxDisks.SizeText(500000000000) + " volume", LinuxDisks.SizeText(7000000000) + " volume"], LinuxDisks.Parse(json).Select(d => d.Name));
        Assert.True(LinuxDisks.LooksLikeVolumeId("1234-ABCD"));
        Assert.False(LinuxDisks.LooksLikeVolumeId("disk1"));
    }

    [Fact]
    public void Without_lsblk_labels_come_from_the_by_label_links()
    {
        const string mounts = "/dev/sdb1 /media/ali/My\\040Disk ext4 rw 0 0\n/dev/sdc1 /mnt/usb vfat rw 0 0\nproc /proc proc rw 0 0\n";
        var labels = LinuxDisks.LabelsByMount(mounts, [("My\\x20Data", "/dev/sdb1"), ("STICK", "/dev/sdc1"), ("Other", "/dev/sdd1")]);
        Assert.Equal("My Data", labels["/media/ali/My Disk"]);
        Assert.Equal("STICK", labels["/mnt/usb"]);
        Assert.Equal(2, labels.Count);
    }

    [Theory]
    [InlineData("/mnt/nas", "nfs4", true)]
    [InlineData("/media/user/share", "cifs", true)]
    [InlineData("/home/user/remote", "fuse.sshfs", true)]
    [InlineData("/run/media/user/dav", "davfs", true)]
    [InlineData("/mnt/c", "9p", true)] // WSL: the Windows drive
    [InlineData("/proc/sys/fs/binfmt_misc", "autofs", false)] // systemd's automount point (.NET calls it a network drive)
    [InlineData("/proc/sys/fs/binfmt_misc", "binfmt_misc", false)]
    [InlineData("/usr/lib/wsl/drivers", "9p", false)] // WSL's own mounts
    [InlineData("/run/user/1000/doc", "fuse.portal", false)]
    [InlineData("/var/lib/docker", "ext4", false)]
    [InlineData("/mnt/backup", "autofs", false)]
    public void Network_shares_are_network_file_systems_outside_system_folders(string path, string fileSystem, bool share) =>
        Assert.Equal(share, LinuxDisks.IsNetworkShare(path, fileSystem));

    [Fact]
    public void Sizes_like_a_file_manager()
    {
        Assert.Equal("500", LinuxDisks.SizeText(500_107_862_016).Split(' ')[0]);
        Assert.EndsWith("GB", LinuxDisks.SizeText(500_107_862_016), StringComparison.Ordinal);
        Assert.EndsWith("TB", LinuxDisks.SizeText(2_000_398_934_016), StringComparison.Ordinal);
    }
}

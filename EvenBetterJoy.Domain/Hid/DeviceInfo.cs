﻿using System.Runtime.InteropServices;

namespace EvenBetterJoy.Domain.Hid
{
    public struct DeviceInfo
    {
        [Obsolete("Path is not well documented or reliable")]
        [MarshalAs(UnmanagedType.LPStr)]
        public string path;
        public ushort vendor_id;
        public ushort product_id;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string serial_number;
        public ushort release_number;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string manufacturer_string;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string product_string;
        public ushort usage_page;
        public ushort usage;
        public int interface_number;
        public IntPtr next;
    }
}

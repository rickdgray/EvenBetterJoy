﻿using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Numerics;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client;
using EvenBetterJoy.Domain.Services;

namespace EvenBetterJoy.Domain.Models
{
    public class Joycon
    {
        public string path = string.Empty;
        public bool isPro = false;
        public bool isSnes = false;
        bool isUSB = false;

        private Joycon _other = null;
        public Joycon Other
        {
            get
            {
                return _other;
            }
            set
            {
                _other = value;

                // If the other Joycon is itself, the Joycon is sideways
                if (_other == null || _other == this)
                {
                    // Set LED to current Pad ID
                    SetLEDByPlayerNum(PadId);
                }
                else
                {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    SetLEDByPlayerNum(lowestPadId);
                }
            }
        }
        public bool active_gyro = false;

        private long inactivity = Stopwatch.GetTimestamp();

        public bool send = true;

        public bool isLeft;

        public ControllerState State { get; set; }
        private ControllerDebugMode debugMode;

        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];
        private long[] buttons_down_timestamp = new long[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        public IntPtr Handle { get; set; }

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private ushort[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private ushort deadzone;
        private ushort[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private ushort[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private ushort deadzone2;
        private ushort[] stick2_precal = { 0, 0 };

        private bool polling = false;
        private bool imu_enabled = false;
        private short[] acc_r = { 0, 0, 0 };
        private short[] acc_neutral = { 0, 0, 0 };
        private short[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g;

        private short[] gyr_r = { 0, 0, 0 };
        private short[] gyr_neutral = { 0, 0, 0 };
        private short[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g;

        private float[] cur_rotation; // Filtered IMU data

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private short[] pro_hor_offset = { -710, 0, 0 };
        private short[] left_hor_offset = { 0, 0, 0 };
        private short[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private Rumble rumble;

        private byte global_count = 0;
        private string debug_str;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;
        ushort ds4_ts = 0;
        ulong lag;

        public byte LED { get; private set; } = 0x0;
        public void SetLEDByPlayerNum(int id)
        {
            if (id > 3)
            {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (settings.UseIncrementalLights)
            {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do
                {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            }
            else
            {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        public string serial_number;

        private float[] activeData;
        private GyroHelper gyroHelper;

        private readonly Settings settings;
        private readonly ILogger logger;
        private readonly IDeviceService deviceService;
        private readonly ICommunicationService communicationService;
        private readonly ViGEmClient client;
        public Joycon(Settings settings, IDeviceService deviceService, ICommunicationService communicationService,
            ViGEmClient client, IntPtr handle_, bool imu, bool localize, float alpha, bool left,
            string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false)
        {
            this.settings = settings;
            //TODO: how to get a ILogger<Joycon>?
            this.deviceService = deviceService;
            this.communicationService = communicationService;
            this.client = client;

            serial_number = serialNum;
            activeData = new float[6];
            Handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble = new Rumble(new float[] { settings.LowFreqRumble, settings.HighFreqRumble, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
            {
                buttons_down_timestamp[i] = -1;
            }
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes;
            this.isSnes = isSnes;
            isUSB = serialNum == "000000000001";

            this.path = path;

            connection = isUSB ? 0x01 : 0x02;

            if (settings.ShowAsXInput)
            {
                out_xbox = new OutputControllerXbox360(client);
                if (settings.EnableRumble)
                {
                    out_xbox.FeedbackReceived += ReceiveRumble;
                }
            }

            if (settings.ShowAsDS4)
            {
                out_ds4 = new OutputControllerDualShock4(client);
                if (settings.EnableRumble)
                {
                    out_ds4.FeedbackReceived += Ds4_FeedbackReceived;
                }
            }

            gyroHelper = new GyroHelper(0.005f, settings.AhrsBeta);
        }

        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e)
        {
            DebugPrint("Rumble data Recived: XInput", ControllerDebugMode.RUMBLE);
            SetRumble(settings.LowFreqRumble, settings.HighFreqRumble, Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (Other != null && Other != this)
            {
                Other.SetRumble(settings.LowFreqRumble, settings.HighFreqRumble, Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
            }
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e)
        {
            DebugPrint("Rumble data Recived: DS4", ControllerDebugMode.RUMBLE);
            SetRumble(settings.LowFreqRumble, settings.HighFreqRumble, Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (Other != null && Other != this)
            {
                Other.SetRumble(settings.LowFreqRumble, settings.HighFreqRumble, Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
            }
        }

        public void DebugPrint(string message, ControllerDebugMode debugMode)
        {
            // if joycon debug mode is none, just force no messages
            if (this.debugMode == ControllerDebugMode.NONE)
            {
                return;
            }

            // otherwise, if message is mode all or of the same type, print
            if (debugMode == ControllerDebugMode.ALL || this.debugMode == debugMode || this.debugMode == ControllerDebugMode.ALL)
            {
                logger.LogDebug(message);
            }
        }

        public Vector3 GetGyro()
        {
            return gyr_g;
        }

        public Vector3 GetAccel()
        {
            return acc_g;
        }

        public int Attach()
        {
            State = ControllerState.ATTACHED;
            
            if (isUSB)
            {
                var a = Enumerable.Repeat((byte)0, 64).ToArray();
                logger.LogInformation("Using USB.");

                a[0] = 0x80;
                a[1] = 0x1;
                deviceService.Write(Handle, a, new UIntPtr(2));
                deviceService.Read(Handle, a, new UIntPtr(64), 100);

                if (a[0] != 0x81)
                {
                    // can occur when USB connection isn't closed properly
                    logger.LogWarning("Resetting USB connection.");
                    Subcommand(0x06, new byte[] { 0x01 }, 1);
                    //TODO: verify this exception is needed
                    throw new Exception("reset_usb");
                }

                if (a[3] == 0x3)
                {
                    PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
                }

                // USB Pairing
                a = Enumerable.Repeat((byte)0, 64).ToArray();

                // Handshake
                a[0] = 0x80;
                a[1] = 0x2;
                deviceService.Write(Handle, a, new UIntPtr(2));
                deviceService.Read(Handle, a, new UIntPtr(64), 100);

                // 3Mbit baud rate
                a[0] = 0x80;
                a[1] = 0x3;
                deviceService.Write(Handle, a, new UIntPtr(2));
                deviceService.Read(Handle, a, new UIntPtr(64), 100);

                // Handshake at new baud rate
                a[0] = 0x80;
                a[1] = 0x2;
                deviceService.Write(Handle, a, new UIntPtr(2));
                deviceService.Read(Handle, a, new UIntPtr(64), 100);

                // Prevent HID timeout
                a[0] = 0x80;
                a[1] = 0x4;
                deviceService.Write(Handle, a, new UIntPtr(2));
                deviceService.Read(Handle, a, new UIntPtr(64), 100);

            }

            LoadCalibrationData();

            // Bluetooth manual pairing
            var btMAC = new PhysicalAddress(new byte[] { 0, 0, 0, 0, 0, 0 });
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Get local BT host MAC
                if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx
                    && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
                    && nic.Name.Split()[0] == "Bluetooth")
                {
                    btMAC = nic.GetPhysicalAddress();
                }
            }

            //TODO: what's up with this variable after finding the nic?
            byte[] btmac_host = btMAC.GetAddressBytes();
            //TODO: what was this for?
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { imu_enabled ? (byte)0x1 : (byte)0x0 }, 1);
            Subcommand(0x48, new byte[] { 0x01 }, 1);

            Subcommand(0x3, new byte[] { 0x30 }, 1);
            DebugPrint("Done with init.", ControllerDebugMode.COMMS);

            deviceService.SetDeviceNonblocking(Handle, 1);

            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0)
        {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight()
        {
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public void SetHomeLight(bool on)
        {
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on)
            {
                a[0] = 0x1F;
                a[1] = 0xF0;
            }
            else
            {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state)
        {
            byte[] a = { state };
            Subcommand(0x06, a, 1);
        }

        public void PowerOff()
        {
            if (State > ControllerState.DROPPED)
            {
                deviceService.SetDeviceNonblocking(Handle, 0);
                SetHCIState(0x00);
                State = ControllerState.DROPPED;
            }
        }

        private void BatteryChanged()
        {
            if (battery <= 1)
            {
                //TODO: figure out how to alert the user
                //string.Format("Controller {0} ({1}) - low battery notification!", PadId, isPro ? "Pro Controller" : (isSnes ? "SNES Controller" : (isLeft ? "Joycon Left" : "Joycon Right")));
            }
        }

        public void Detach(bool close = false)
        {
            polling = false;

            if (out_xbox != null)
            {
                out_xbox.Disconnect();
            }

            if (out_ds4 != null)
            {
                out_ds4.Disconnect();
            }

            if (State > ControllerState.NO_JOYCONS)
            {
                deviceService.SetDeviceNonblocking(Handle, 0);

                //Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
                //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

                if (isUSB)
                {
                    // Allow device to talk to BT again
                    byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
                    a[0] = 0x80; a[1] = 0x5;
                    deviceService.Write(Handle, a, new UIntPtr(2));
                    a[0] = 0x80; a[1] = 0x6;
                    deviceService.Write(Handle, a, new UIntPtr(2));
                }
            }

            if (close || State > ControllerState.DROPPED)
            {
                deviceService.CloseDevice(Handle);
            }

            State = ControllerState.NOT_ATTACHED;
        }

        private byte ts_en;
        private int ReceiveRaw()
        {
            if (Handle == IntPtr.Zero)
            {
                return -2;
            }
            
            var raw_buf = new byte[report_len];
            var inboundData = deviceService.Read(Handle, raw_buf, new UIntPtr(report_len), 5);

            if (inboundData > 0)
            {
                // Process packets as soon as they come
                for (var n = 0; n < 3; n++)
                {
                    ExtractIMUValues(raw_buf, n);

                    var lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0)
                    {
                        // add lag once
                        Timestamp += (ulong)lag * 5000;
                        ProcessButtonsAndStick(raw_buf);

                        // process buttons here to have them affect DS4
                        DoThingsWithButtons();

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                        {
                            BatteryChanged();
                        }
                    }
                    
                    Timestamp += 5000;
                    packetCounter++;
                    
                    if (!isUSB)
                    {
                        communicationService.NewReportIncoming(this);
                    }

                    if (out_ds4 != null)
                    {
                        try
                        {
                            out_ds4.UpdateInput(MapToDualShock4Input(this));
                        }
                        catch (Exception e)
                        {
                            logger.LogTrace(e.Message);
                        }
                    }
                }

                // no reason to send XInput reports so often
                if (out_xbox != null)
                {
                    try
                    {
                        out_xbox.UpdateInput(MapToXbox360Input(this));
                    }
                    catch (Exception e)
                    {
                        logger.LogTrace(e.Message);
                    }
                }

                //TODO: why filter out snes only?
                if (ts_en == raw_buf[1] && !isSnes)
                {
                    logger.LogTrace("Duplicate timestamp enqueued.");
                    DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), ControllerDebugMode.THREADING);
                }

                ts_en = raw_buf[1];
                DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", inboundData, raw_buf[1]), ControllerDebugMode.THREADING);
            }

            return inboundData;
        }

        Dictionary<int, bool> mouse_toggle_btn = new Dictionary<int, bool>();
        private void Simulate(string s, bool click = true, bool up = false)
        {
            //TODO: get rid of this string parsing hack
            //TODO: try out Desktop.Robot for os agnostic key simulation
            //if (s.StartsWith("key_"))
            //{
            //    WindowsInput.Events.KeyCode key = (WindowsInput.Events.KeyCode)Int32.Parse(s.Substring(4));
            //    if (click)
            //    {
            //        WindowsInput.Simulate.Events().Click(key).Invoke();
            //    }
            //    else
            //    {
            //        if (up)
            //        {
            //            WindowsInput.Simulate.Events().Release(key).Invoke();
            //        }
            //        else
            //        {
            //            WindowsInput.Simulate.Events().Hold(key).Invoke();
            //        }
            //    }
            //}
            //else if (s.StartsWith("mse_"))
            //{
            //    WindowsInput.Events.ButtonCode button = (WindowsInput.Events.ButtonCode)Int32.Parse(s.Substring(4));
            //    if (click)
            //    {
            //        WindowsInput.Simulate.Events().Click(button).Invoke();
            //    }
            //    else
            //    {
            //        if (settings.DragToggle)
            //        {
            //            if (!up)
            //            {
            //                mouse_toggle_btn.TryGetValue((int)button, out bool release);
            //                if (release)
            //                    WindowsInput.Simulate.Events().Release(button).Invoke();
            //                else
            //                    WindowsInput.Simulate.Events().Hold(button).Invoke();
            //                mouse_toggle_btn[(int)button] = !release;
            //            }
            //        }
            //        else
            //        {
            //            if (up)
            //            {
            //                WindowsInput.Simulate.Events().Release(button).Invoke();
            //            }
            //            else
            //            {
            //                WindowsInput.Simulate.Events().Hold(button).Invoke();
            //            }
            //        }
            //    }
            //}
        }

        // For Joystick->Joystick inputs
        private void SimulateContinous(int origin, string s)
        {
            if (s.StartsWith("joy_"))
            {
                int button = int.Parse(s.Substring(4));
                buttons[button] |= buttons[origin];
            }
        }
        
        long lastDoubleClick = -1;
        byte[] sliderVal = new byte[] { 0, 0 };
        private void DoThingsWithButtons()
        {
            int powerOffButton = (int)((isPro || !isLeft || Other != null) ? ControllerButton.HOME : ControllerButton.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (settings.HomeLongPowerOff && buttons[powerOffButton])
            {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0)
                {
                    if (Other != null)
                    {
                        Other.PowerOff();
                    }

                    PowerOff();
                    return;
                }
            }

            if (settings.ChangeOrientationDoubleClick && buttons_down[(int)ControllerButton.STICK] && lastDoubleClick != -1 && !isPro)
            {
                if ((buttons_down_timestamp[(int)ControllerButton.STICK] - lastDoubleClick) < 3000000)
                {
                    //TODO: this is disgusting
                    // trigger connection button click
                    //form.conBtnClick(form.con[PadId], EventArgs.Empty);

                    lastDoubleClick = buttons_down_timestamp[(int)ControllerButton.STICK];
                    return;
                }

                lastDoubleClick = buttons_down_timestamp[(int)ControllerButton.STICK];
            }
            else if (settings.ChangeOrientationDoubleClick && buttons_down[(int)ControllerButton.STICK] && !isPro)
            {
                lastDoubleClick = buttons_down_timestamp[(int)ControllerButton.STICK];
            }

            if (settings.PowerOffInactivity > 0)
            {
                if ((timestamp - inactivity) / 10000 > settings.PowerOffInactivity * 60 * 1000)
                {
                    if (Other != null)
                    {
                        Other.PowerOff();
                    }

                    PowerOff();
                    return;
                }
            }

            //DetectShake();

            if (buttons_down[(int)ControllerButton.CAPTURE])
            {
                Simulate(settings.Capture);
            }
            
            if (buttons_down[(int)ControllerButton.HOME])
            {
                Simulate(settings.Home);
            }
            
            SimulateContinous((int)ControllerButton.CAPTURE, settings.Capture);
            SimulateContinous((int)ControllerButton.HOME, settings.Home);

            if (isLeft)
            {
                if (buttons_down[(int)ControllerButton.SL])
                {
                    Simulate(settings.LeftJoyconL, false, false);
                }
                
                if (buttons_up[(int)ControllerButton.SL])
                {
                    Simulate(settings.LeftJoyconL, false, true);
                }
                
                if (buttons_down[(int)ControllerButton.SR])
                {
                    Simulate(settings.LeftJoyconR, false, false);
                }
                
                if (buttons_up[(int)ControllerButton.SR])
                {
                    Simulate(settings.LeftJoyconR, false, true);
                }

                SimulateContinous((int)ControllerButton.SL, settings.LeftJoyconL);
                SimulateContinous((int)ControllerButton.SR, settings.LeftJoyconR);
            }
            else
            {
                if (buttons_down[(int)ControllerButton.SL])
                {
                    Simulate(settings.RightJoyconL, false, false);
                }
                
                if (buttons_up[(int)ControllerButton.SL])
                {
                    Simulate(settings.RightJoyconL, false, true);
                }
                
                if (buttons_down[(int)ControllerButton.SR])
                {
                    Simulate(settings.RightJoyconR, false, false);
                }

                if (buttons_up[(int)ControllerButton.SR])
                {
                    Simulate(settings.RightJoyconR, false, true);
                }

                SimulateContinous((int)ControllerButton.SL, settings.RightJoyconL);
                SimulateContinous((int)ControllerButton.SR, settings.RightJoyconR);
            }

            // Filtered IMU data
            cur_rotation = gyroHelper.GetEulerAngles();
            float dt = 0.015f; // 15ms

            if (settings.GyroAnalogSliders && (Other != null || isPro))
            {
                ControllerButton leftT = isLeft ? ControllerButton.SHOULDER_2 : ControllerButton.SHOULDER2_2;
                ControllerButton rightT = isLeft ? ControllerButton.SHOULDER2_2 : ControllerButton.SHOULDER_2;
                Joycon left = isLeft ? this : (isPro ? this : Other); Joycon right = !isLeft ? this : (isPro ? this : Other);

                int ldy, rdy;
                if (settings.UseFilteredIMU)
                {
                    ldy = (int)(settings.GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(settings.GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                }
                else
                {
                    ldy = (int)(settings.GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(settings.GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT])
                {
                    sliderVal[0] = (byte)Math.Min(byte.MaxValue, Math.Max(0, sliderVal[0] + ldy));
                }
                else
                {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT])
                {
                    sliderVal[1] = (byte)Math.Min(byte.MaxValue, Math.Max(0, sliderVal[1] + rdy));
                }
                else
                {
                    sliderVal[1] = 0;
                }
            }

            string res_val = settings.ActiveGyro;
            if (res_val.StartsWith("joy_"))
            {
                var i = int.Parse(res_val.Substring(4));
                if (settings.GyroHoldToggle)
                {
                    if (buttons_down[i] || (Other != null && Other.buttons_down[i]))
                    {
                        active_gyro = true;
                    }
                    else if (buttons_up[i] || (Other != null && Other.buttons_up[i]))
                    {
                        active_gyro = false;
                    }
                }
                else
                {
                    if (buttons_down[i] || (Other != null && Other.buttons_down[i]))
                    {
                        active_gyro = !active_gyro;
                    }
                }
            }

            if (settings.GyroToJoyOrMouse[..3] == "joy")
            {
                if (settings.ActiveGyro == "0" || active_gyro)
                {
                    float[] control_stick = (settings.GyroToJoyOrMouse == "joy_left") ? stick : stick2;

                    float dx, dy;
                    if (settings.UseFilteredIMU)
                    {
                        dx = (settings.GyroStickSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                        dy = -(settings.GyroStickSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
                    }
                    else
                    {
                        dx = (settings.GyroStickSensitivityX * (gyr_g.Z * dt)); // yaw
                        dy = -(settings.GyroStickSensitivityY * (gyr_g.Y * dt)); // pitch
                    }

                    control_stick[0] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[0] / settings.GyroStickReduction + dx));
                    control_stick[1] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[1] / settings.GyroStickReduction + dy));
                }
            }
            //TODO: probably gonna throw this away if my assumption is correct
            //that it's just for controlling gyro with mouse
            //else if (settings.GyroToJoyOrMouse == "mouse" && (isPro || (Other == null) || (Other != null && (settings.GyroMouseLeftHanded ? isLeft : !isLeft))))
            //{
            //    // gyro data is in degrees/s
            //    if (settings.ActiveGyro == "0" || active_gyro)
            //    {
            //        int dx, dy;

            //        if (settings.UseFilteredIMU)
            //        {
            //            dx = (int)(settings.GyroMouseSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
            //            dy = (int)-(settings.GyroMouseSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
            //        }
            //        else
            //        {
            //            dx = (int)(settings.GyroMouseSensitivityX * (gyr_g.Z * dt));
            //            dy = (int)-(settings.GyroMouseSensitivityY * (gyr_g.Y * dt));
            //        }

            //        //robot.MouseMove(dx, dy);
            //        WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
            //    }

            //    // reset mouse position to centre of primary monitor
            //    res_val = settings.ResetMouse;
            //    if (res_val.StartsWith("joy_"))
            //    {
            //        int i = int.Parse(res_val[4..]);
            //        if (buttons_down[i] || (Other != null && Other.buttons_down[i]))
            //        {
            //            WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
            //        }
            //    }
            //}
        }

        private Thread PollThread;
        private void Poll()
        {
            polling = true;
            int attempts = 0;
            while (polling & State > ControllerState.NO_JOYCONS)
            {
                if (rumble.queue.Count > 0)
                {
                    SendRumble(rumble.GetData());
                }
                int a = ReceiveRaw();

                if (a > 0 && State > ControllerState.DROPPED)
                {
                    State = ControllerState.IMU_DATA_OK;
                    attempts = 0;
                }
                else if (attempts > 240)
                {
                    State = ControllerState.DROPPED;
                    logger.LogInformation("Dropped.");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", ControllerDebugMode.ALL);
                    break;
                }
                else if (a < 0)
                {
                    // An error on read.
                    Thread.Sleep(5);
                    attempts++;
                }
                else if (a == 0)
                {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }
            }
        }

        public float[] otherStick = { 0, 0 };
        private int ProcessButtonsAndStick(byte[] report_buf)
        {
            if (report_buf[0] == 0x00)
            {
                throw new ArgumentException("received undefined report. This is probably a bug");
            }

            if (!isSnes)
            {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (isPro)
                {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (ushort)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (ushort)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                stick = CalculateStickData(stick_precal, stick_cal, deadzone);

                if (isPro)
                {
                    stick2_precal[0] = (ushort)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (ushort)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    stick2 = CalculateStickData(stick2_precal, stick2_cal, deadzone2);
                }

                // Read other Joycon's sticks
                if (isLeft && Other != null && Other != this)
                {
                    stick2 = otherStick;
                    Other.otherStick = stick;
                }

                if (!isLeft && Other != null && Other != this)
                {
                    Array.Copy(stick, stick2, 2);
                    stick = otherStick;
                    Other.otherStick = stick2;
                }
            }

            // Set button states both for server and ViGEm
            lock (buttons)
            {
                lock (down_)
                {
                    for (int i = 0; i < buttons.Length; ++i)
                    {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];

                //TODO: convert all this to list of enums instead of this crap array of casted enums
                buttons[(int)ControllerButton.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)ControllerButton.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)ControllerButton.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)ControllerButton.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)ControllerButton.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)ControllerButton.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)ControllerButton.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)ControllerButton.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)ControllerButton.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)ControllerButton.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
                buttons[(int)ControllerButton.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
                buttons[(int)ControllerButton.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)ControllerButton.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (isPro)
                {
                    buttons[(int)ControllerButton.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)ControllerButton.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)ControllerButton.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)ControllerButton.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)ControllerButton.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)ControllerButton.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)ControllerButton.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                }

                if (Other != null && Other != this)
                {
                    buttons[(int)(ControllerButton.B)] = Other.buttons[(int)ControllerButton.DPAD_DOWN];
                    buttons[(int)(ControllerButton.A)] = Other.buttons[(int)ControllerButton.DPAD_RIGHT];
                    buttons[(int)(ControllerButton.X)] = Other.buttons[(int)ControllerButton.DPAD_UP];
                    buttons[(int)(ControllerButton.Y)] = Other.buttons[(int)ControllerButton.DPAD_LEFT];

                    buttons[(int)ControllerButton.STICK2] = Other.buttons[(int)ControllerButton.STICK];
                    buttons[(int)ControllerButton.SHOULDER2_1] = Other.buttons[(int)ControllerButton.SHOULDER_1];
                    buttons[(int)ControllerButton.SHOULDER2_2] = Other.buttons[(int)ControllerButton.SHOULDER_2];
                }

                if (isLeft && Other != null && Other != this)
                {
                    buttons[(int)ControllerButton.HOME] = Other.buttons[(int)ControllerButton.HOME];
                    buttons[(int)ControllerButton.PLUS] = Other.buttons[(int)ControllerButton.PLUS];
                }

                if (!isLeft && Other != null && Other != this)
                {
                    buttons[(int)ControllerButton.MINUS] = Other.buttons[(int)ControllerButton.MINUS];
                }

                long timestamp = Stopwatch.GetTimestamp();

                lock (buttons_up)
                {
                    lock (buttons_down)
                    {
                        bool changed = false;
                        for (int i = 0; i < buttons.Length; ++i)
                        {
                            buttons_up[i] = down_[i] & !buttons[i];
                            buttons_down[i] = !down_[i] & buttons[i];
                            
                            if (down_[i] != buttons[i])
                            {
                                buttons_down_timestamp[i] = buttons[i] ? timestamp : -1;
                            }
                            
                            if (buttons_up[i] || buttons_down[i])
                            {
                                changed = true;
                            }
                        }

                        inactivity = changed ? timestamp : inactivity;
                    }
                }
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0)
        {
            if (!isSnes)
            {
                gyr_r[0] = (short)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (short)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (short)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (short)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (short)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (short)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                //TODO: figure out where to put user input for cal data
                if (false) //(form.allowCalibration)
                {
                    //for (int i = 0; i < 3; ++i)
                    //{
                    //    switch (i)
                    //    {
                    //        case 0:
                    //            acc_g.X = (acc_r[i] - activeData[3]) * (1.0f / acc_sen[i]) * 4.0f;
                    //            gyr_g.X = (gyr_r[i] - activeData[0]) * (816.0f / gyr_sen[i]);
                    //            if (form.calibrate)
                    //            {
                    //                form.xA.Add(acc_r[i]);
                    //                form.xG.Add(gyr_r[i]);
                    //            }
                    //            break;
                    //        case 1:
                    //            acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[4]) * (1.0f / acc_sen[i]) * 4.0f;
                    //            gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[1]) * (816.0f / gyr_sen[i]);
                    //            if (form.calibrate)
                    //            {
                    //                form.yA.Add(acc_r[i]);
                    //                form.yG.Add(gyr_r[i]);
                    //            }
                    //            break;
                    //        case 2:
                    //            acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[5]) * (1.0f / acc_sen[i]) * 4.0f;
                    //            gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[2]) * (816.0f / gyr_sen[i]);
                    //            if (form.calibrate)
                    //            {
                    //                form.zA.Add(acc_r[i]);
                    //                form.zG.Add(gyr_r[i]);
                    //            }
                    //            break;
                    //    }
                    //}
                }
                else
                {
                    short[] offset;
                    if (isPro)
                    {
                        offset = pro_hor_offset;
                    }
                    else if (isLeft)
                    {
                        offset = left_hor_offset;
                    }
                    else
                    {
                        offset = right_hor_offset;
                    }

                    for (var i = 0; i < 3; ++i)
                    {
                        switch (i)
                        {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                if (Other == null && !isPro)
                {
                    // single joycon mode; Z do not swap, rest do
                    if (isLeft)
                    {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    }
                    else
                    {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    var temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Update rotation Quaternion
                var deg_to_rad = 0.0174533f;
                gyroHelper.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        public void Begin()
        {
            if (PollThread == null)
            {
                PollThread = new Thread(new ThreadStart(Poll))
                {
                    IsBackground = true
                };

                logger.LogInformation("Starting poll thread.");
                PollThread.Start();
                logger.LogInformation("Started poll thread.");
            }
            else
            {
                throw new NullReferenceException("Polling thread is null; cannot start.");
            }
        }

        private static float[] CalculateStickData(ushort[] vals, ushort[] cal, ushort dz)
        {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
            {
                return s;
            }

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);
            
            return s;
        }

        //TODO: combines these into T generic return probably using a switch expression
        private static short CastStickValue(float stick_value)
        {
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, stick_value * (stick_value > 0 ? short.MaxValue : -short.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value)
        {
            return (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, 127 - stick_value * byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp)
        {
            if (State <= ControllerState.ATTACHED)
            {
                return;
            }

            rumble.SetVals(low_freq, high_freq, amp);
        }

        private void SendRumble(byte[] buf)
        {
            var buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;

            if (global_count == 0xf)
            {
                global_count = 0;
            }
            else
            {
                global_count++;
            }

            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, ControllerDebugMode.RUMBLE, format: "Rumble data sent: {0:S}");
            deviceService.Write(Handle, buf_, new UIntPtr(report_len));
        }

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true)
        {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;

            if (global_count == 0xf)
            {
                global_count = 0;
            }
            else
            {
                global_count++;
            }

            if (print)
            {
                PrintArray(buf_, ControllerDebugMode.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}");
            }

            deviceService.Write(Handle, buf_, new UIntPtr(len + 11));

            //TODO: does this really need to be a do while?
            int tries = 0;
            do
            {
                int res = deviceService.Read(Handle, response, new UIntPtr(report_len), 100);
                if (res < 1)
                {
                    DebugPrint("No response.", ControllerDebugMode.COMMS);
                }
                else if (print)
                {
                    PrintArray(response, ControllerDebugMode.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}");
                }

                tries++;
            } while (tries < 10 && response[0] != 0x21 && response[14] != sc);

            return response;
        }

        private void LoadCalibrationData()
        {
            if (isSnes)
            {
                //TODO: get rid of this string parsing crap
                short[] temp = settings.acc_sensiti.Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0];
                acc_sensiti[1] = temp[1];
                acc_sensiti[2] = temp[2];

                temp = settings.gyr_sensiti.Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0];
                gyr_sensiti[1] = temp[1];
                gyr_sensiti[2] = temp[2];

                ushort[] temp2 = settings.stick_cal.Split(',').Select(s => ushort.Parse(s[2..], NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0];
                stick_cal[1] = temp2[1];
                stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3];
                stick_cal[4] = temp2[4];
                stick_cal[5] = temp2[5];

                temp2 = settings.stick2_cal.Split(',').Select(s => ushort.Parse(s[2..], NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0];
                stick2_cal[1] = temp2[1];
                stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3];
                stick2_cal[4] = temp2[4];
                stick2_cal[5] = temp2[5];

                deadzone = settings.deadzone;
                deadzone2 = settings.deadzone2;

                return;
            }

            deviceService.SetDeviceNonblocking(Handle, 0);

            byte[] buf_ = ReadSPI(0x80, isLeft ? (byte)0x12 : (byte)0x1d, 9);
            bool found = false;
            for (int i = 0; i < 9; ++i)
            {
                if (buf_[i] != 0xff)
                {
                    logger.LogInformation("Using user stick calibration data.");
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                logger.LogInformation("Using factory stick calibration data.");
                buf_ = ReadSPI(0x60, isLeft ? (byte)0x3d : (byte)0x46, 9);
            }

            stick_cal[isLeft ? 0 : 2] = (ushort)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (ushort)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (ushort)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (ushort)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (ushort)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (ushort)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (isPro)
            {
                buf_ = ReadSPI(0x80, !isLeft ? (byte)0x12 : (byte)0x1d, 9);
                found = false;
                for (int i = 0; i < 9; ++i)
                {
                    if (buf_[i] != 0xff)
                    {
                        logger.LogInformation("Using user stick calibration data.");
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    logger.LogInformation("Using factory stick calibration data.");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9);
                }

                stick2_cal[!isLeft ? 0 : 2] = (ushort)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (ushort)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (ushort)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (ushort)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (ushort)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (ushort)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, !isLeft ? (byte)0x86 : (byte)0x98, 16);
                deadzone2 = (ushort)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, isLeft ? (byte)0x86 : (byte)0x98, 16);
            deadzone = (ushort)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: ControllerDebugMode.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100)
            {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (short)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (short)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (short)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: ControllerDebugMode.IMU, format: "Factory gyro neutral position: {0:S}");
            }

            deviceService.SetDeviceNonblocking(Handle, 1);
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false)
        {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i)
            {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1)
                {
                    break;
                }
            }

            Array.Copy(buf_, 20, read_buf, 0, len);

            if (print)
            {
                PrintArray(read_buf, ControllerDebugMode.COMMS, len);
            }

            return read_buf;
        }

        private void PrintArray<T>(T[] arr, ControllerDebugMode d = ControllerDebugMode.NONE, uint len = 0, uint start = 0, string format = "{0:S}")
        {
            if (d != debugMode && debugMode != ControllerDebugMode.ALL)
            {
                return;
            }

            if (len == 0)
            {
                len = (uint)arr.Length;
            }

            string tostr = "";
            for (int i = 0; i < len; ++i)
            {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }

            DebugPrint(string.Format(format, tostr), d);
        }

        private OutputControllerXbox360InputState MapToXbox360Input(Joycon input)
        {
            var output = new OutputControllerXbox360InputState();

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.Other;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (isPro)
            {
                output.a = buttons[(int)(!settings.SwapAB ? ControllerButton.B : ControllerButton.A)];
                output.b = buttons[(int)(!settings.SwapAB ? ControllerButton.A : ControllerButton.B)];
                output.y = buttons[(int)(!settings.SwapXY ? ControllerButton.X : ControllerButton.Y)];
                output.x = buttons[(int)(!settings.SwapXY ? ControllerButton.Y : ControllerButton.X)];

                output.dpad_up = buttons[(int)ControllerButton.DPAD_UP];
                output.dpad_down = buttons[(int)ControllerButton.DPAD_DOWN];
                output.dpad_left = buttons[(int)ControllerButton.DPAD_LEFT];
                output.dpad_right = buttons[(int)ControllerButton.DPAD_RIGHT];

                output.back = buttons[(int)ControllerButton.MINUS];
                output.start = buttons[(int)ControllerButton.PLUS];
                output.guide = buttons[(int)ControllerButton.HOME];

                output.shoulder_left = buttons[(int)ControllerButton.SHOULDER_1];
                output.shoulder_right = buttons[(int)ControllerButton.SHOULDER2_1];

                output.thumb_stick_left = buttons[(int)ControllerButton.STICK];
                output.thumb_stick_right = buttons[(int)ControllerButton.STICK2];
            }
            else
            {
                if (other != null)
                {
                    // no need for && other != this
                    output.a = buttons[(int)(!settings.SwapAB ? isLeft ? ControllerButton.B : ControllerButton.DPAD_DOWN : isLeft ? ControllerButton.A : ControllerButton.DPAD_RIGHT)];
                    output.b = buttons[(int)(settings.SwapAB ? isLeft ? ControllerButton.B : ControllerButton.DPAD_DOWN : isLeft ? ControllerButton.A : ControllerButton.DPAD_RIGHT)];
                    output.y = buttons[(int)(!settings.SwapXY ? isLeft ? ControllerButton.X : ControllerButton.DPAD_UP : isLeft ? ControllerButton.Y : ControllerButton.DPAD_LEFT)];
                    output.x = buttons[(int)(settings.SwapXY ? isLeft ? ControllerButton.X : ControllerButton.DPAD_UP : isLeft ? ControllerButton.Y : ControllerButton.DPAD_LEFT)];

                    output.dpad_up = buttons[(int)(isLeft ? ControllerButton.DPAD_UP : ControllerButton.X)];
                    output.dpad_down = buttons[(int)(isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.B)];
                    output.dpad_left = buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.Y)];
                    output.dpad_right = buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.A)];

                    output.back = buttons[(int)ControllerButton.MINUS];
                    output.start = buttons[(int)ControllerButton.PLUS];
                    output.guide = buttons[(int)ControllerButton.HOME];

                    output.shoulder_left = buttons[(int)(isLeft ? ControllerButton.SHOULDER_1 : ControllerButton.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? ControllerButton.SHOULDER2_1 : ControllerButton.SHOULDER_1)];

                    output.thumb_stick_left = buttons[(int)(isLeft ? ControllerButton.STICK : ControllerButton.STICK2)];
                    output.thumb_stick_right = buttons[(int)(isLeft ? ControllerButton.STICK2 : ControllerButton.STICK)];
                }
                else
                {
                    // single joycon mode
                    output.a = buttons[(int)(!settings.SwapAB ? isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.DPAD_RIGHT : isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.DPAD_UP)];
                    output.b = buttons[(int)(settings.SwapAB ? isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.DPAD_RIGHT : isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.DPAD_UP)];
                    output.y = buttons[(int)(!settings.SwapXY ? isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.DPAD_LEFT : isLeft ? ControllerButton.DPAD_UP : ControllerButton.DPAD_DOWN)];
                    output.x = buttons[(int)(settings.SwapXY ? isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.DPAD_LEFT : isLeft ? ControllerButton.DPAD_UP : ControllerButton.DPAD_DOWN)];

                    output.back = buttons[(int)ControllerButton.MINUS] | buttons[(int)ControllerButton.HOME];
                    output.start = buttons[(int)ControllerButton.PLUS] | buttons[(int)ControllerButton.CAPTURE];

                    output.shoulder_left = buttons[(int)ControllerButton.SL];
                    output.shoulder_right = buttons[(int)ControllerButton.SR];

                    output.thumb_stick_left = buttons[(int)ControllerButton.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (settings.Home != "0")
            {
                output.guide = false;
            }

            if (!isSnes)
            {
                if (other != null || isPro)
                { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                }
                else
                { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }

            if (other != null || isPro)
            {
                byte lval = settings.GyroAnalogSliders ? sliderVal[0] : byte.MaxValue;
                byte rval = settings.GyroAnalogSliders ? sliderVal[1] : byte.MaxValue;
                output.trigger_left = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_2 : ControllerButton.SHOULDER2_2)] ? lval : 0);
                output.trigger_right = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER2_2 : ControllerButton.SHOULDER_2)] ? rval : 0);
            }
            else
            {
                output.trigger_left = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_2 : ControllerButton.SHOULDER_1)] ? byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_1 : ControllerButton.SHOULDER_2)] ? byte.MaxValue : 0);
            }

            return output;
        }

        public OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input)
        {
            var output = new OutputControllerDualShock4InputState();

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.Other;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (isPro)
            {
                output.cross = buttons[(int)(!settings.SwapAB ? ControllerButton.B : ControllerButton.A)];
                output.circle = buttons[(int)(!settings.SwapAB ? ControllerButton.A : ControllerButton.B)];
                output.triangle = buttons[(int)(!settings.SwapXY ? ControllerButton.X : ControllerButton.Y)];
                output.square = buttons[(int)(!settings.SwapXY ? ControllerButton.Y : ControllerButton.X)];


                if (buttons[(int)ControllerButton.DPAD_UP])
                {
                    if (buttons[(int)ControllerButton.DPAD_LEFT])
                    {
                        output.dPad = ControllerDpadDirection.Northwest;
                    }
                    else if (buttons[(int)ControllerButton.DPAD_RIGHT])
                    {
                        output.dPad = ControllerDpadDirection.Northeast;
                    }
                    else
                    {
                        output.dPad = ControllerDpadDirection.North;
                    }
                }
                else if (buttons[(int)ControllerButton.DPAD_DOWN])
                {
                    if (buttons[(int)ControllerButton.DPAD_LEFT])
                    {
                        output.dPad = ControllerDpadDirection.Southwest;
                    }
                    else if (buttons[(int)ControllerButton.DPAD_RIGHT])
                    {
                        output.dPad = ControllerDpadDirection.Southeast;
                    }
                    else
                    {
                        output.dPad = ControllerDpadDirection.South;
                    }
                }
                else if (buttons[(int)ControllerButton.DPAD_LEFT])
                {
                    output.dPad = ControllerDpadDirection.West;
                }
                else if (buttons[(int)ControllerButton.DPAD_RIGHT])
                {
                    output.dPad = ControllerDpadDirection.East;
                }

                output.share = buttons[(int)ControllerButton.CAPTURE];
                output.options = buttons[(int)ControllerButton.PLUS];
                output.ps = buttons[(int)ControllerButton.HOME];
                output.touchpad = buttons[(int)ControllerButton.MINUS];
                output.shoulder_left = buttons[(int)ControllerButton.SHOULDER_1];
                output.shoulder_right = buttons[(int)ControllerButton.SHOULDER2_1];
                output.thumb_left = buttons[(int)ControllerButton.STICK];
                output.thumb_right = buttons[(int)ControllerButton.STICK2];
            }
            else
            {
                if (other != null)
                {
                    //TODO: wtf is this useless comment trying to tell me
                    // no need for && other != this
                    output.cross = !settings.SwapAB ? buttons[(int)(isLeft ? ControllerButton.B : ControllerButton.DPAD_DOWN)] : buttons[(int)(isLeft ? ControllerButton.A : ControllerButton.DPAD_RIGHT)];
                    output.circle = settings.SwapAB ? buttons[(int)(isLeft ? ControllerButton.B : ControllerButton.DPAD_DOWN)] : buttons[(int)(isLeft ? ControllerButton.A : ControllerButton.DPAD_RIGHT)];
                    output.triangle = !settings.SwapXY ? buttons[(int)(isLeft ? ControllerButton.X : ControllerButton.DPAD_UP)] : buttons[(int)(isLeft ? ControllerButton.Y : ControllerButton.DPAD_LEFT)];
                    output.square = settings.SwapXY ? buttons[(int)(isLeft ? ControllerButton.X : ControllerButton.DPAD_UP)] : buttons[(int)(isLeft ? ControllerButton.Y : ControllerButton.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? ControllerButton.DPAD_UP : ControllerButton.X)])
                    {
                        if (buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.Y)])
                        {
                            output.dPad = ControllerDpadDirection.Northwest;
                        }
                        else if (buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.A)])
                        {
                            output.dPad = ControllerDpadDirection.Northeast;
                        }
                        else
                        {
                            output.dPad = ControllerDpadDirection.North;
                        }
                    }
                    else if (buttons[(int)(isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.B)])
                    {
                        if (buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.Y)])
                        {
                            output.dPad = ControllerDpadDirection.Southwest;
                        }
                        else if (buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.A)])
                        {
                            output.dPad = ControllerDpadDirection.Southeast;
                        }
                        else
                        {
                            output.dPad = ControllerDpadDirection.South;
                        }
                    }
                    else if (buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.Y)])
                    {
                        output.dPad = ControllerDpadDirection.West;
                    }
                    else if (buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.A)])
                    {
                        output.dPad = ControllerDpadDirection.East;
                    }

                    output.share = buttons[(int)ControllerButton.CAPTURE];
                    output.options = buttons[(int)ControllerButton.PLUS];
                    output.ps = buttons[(int)ControllerButton.HOME];
                    output.touchpad = buttons[(int)ControllerButton.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? ControllerButton.SHOULDER_1 : ControllerButton.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? ControllerButton.SHOULDER2_1 : ControllerButton.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? ControllerButton.STICK : ControllerButton.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? ControllerButton.STICK2 : ControllerButton.STICK)];
                }
                else
                {
                    // single joycon mode
                    output.cross = !settings.SwapAB ? buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.DPAD_RIGHT)] : buttons[(int)(isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.DPAD_UP)];
                    output.circle = settings.SwapAB ? buttons[(int)(isLeft ? ControllerButton.DPAD_LEFT : ControllerButton.DPAD_RIGHT)] : buttons[(int)(isLeft ? ControllerButton.DPAD_DOWN : ControllerButton.DPAD_UP)];
                    output.triangle = !settings.SwapXY ? buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.DPAD_LEFT)] : buttons[(int)(isLeft ? ControllerButton.DPAD_UP : ControllerButton.DPAD_DOWN)];
                    output.square = settings.SwapXY ? buttons[(int)(isLeft ? ControllerButton.DPAD_RIGHT : ControllerButton.DPAD_LEFT)] : buttons[(int)(isLeft ? ControllerButton.DPAD_UP : ControllerButton.DPAD_DOWN)];

                    output.ps = buttons[(int)ControllerButton.MINUS] | buttons[(int)ControllerButton.HOME];
                    output.options = buttons[(int)ControllerButton.PLUS] | buttons[(int)ControllerButton.CAPTURE];

                    output.shoulder_left = buttons[(int)ControllerButton.SL];
                    output.shoulder_right = buttons[(int)ControllerButton.SR];

                    output.thumb_left = buttons[(int)ControllerButton.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (settings.Home != "0")
            {
                output.ps = false;
            }

            if (!isSnes)
            {
                if (other != null || isPro)
                {
                    // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                }
                else
                {
                    // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (other != null || isPro)
            {
                byte lval = settings.GyroAnalogSliders ? sliderVal[0] : byte.MaxValue;
                byte rval = settings.GyroAnalogSliders ? sliderVal[1] : byte.MaxValue;
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_2 : ControllerButton.SHOULDER2_2)] ? lval : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER2_2 : ControllerButton.SHOULDER_2)] ? rval : 0);
            }
            else
            {
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_2 : ControllerButton.SHOULDER_1)] ? byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? ControllerButton.SHOULDER_1 : ControllerButton.SHOULDER_2)] ? byte.MaxValue : 0);
            }

            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;

            return output;
        }
    }
}

// =====================================================================================
//  meadowBTport - Meadow F7 Feather V2 solar data sender (Bluetooth LE + serial)
// =====================================================================================
//  Every second this app reads 6 analog inputs and 4 digital inputs, builds one text
//  packet and sends it two ways:
//    [BLUETOOTH] stores it in a BLE characteristic that MauiMVVMbt20261018 reads
//    [SERIAL]    writes it to the COM1 UART (the original serial path used by MauiMvvmUSB)
//
//  It also receives output-control packets that turn D06-D09 on and off:
//    [RECEIVE]   over Bluetooth, from the Send button in MauiMVVMbt20261018
//    [SERIAL]    over COM1, from the USB apps
//
//  Tags used in the comments (search for them):
//    [BLUETOOTH]  BLE GATT server: definition, packet characteristic, ValueSet handler
//    [RECEIVE]    Bluetooth receive: control packets written to the characteristic set D06-D09
//    [SERIAL]     COM1 UART: sends packets, receives output-control packets for D06-D09
//    [ANALOG]     A00-A05 analog inputs, in millivolts
//    [DIGITAL]    D02-D05 inputs and D06-D09 outputs
//    [PACKET]     building the packet (the state machine in SendLoop)
//    [LED]        onboard RGB LED heartbeat
//
//  FILES
//    MeadowApp.cs          this file - all of the application code
//    meadowBTport.csproj   Meadow project: netstandard2.1 library named "App" plus the Meadow packages
//    meadow.config.yaml    device settings (Device Name: MeadowSolar)
//    app.config.yaml       app lifecycle (restart on failure) and logging level
//    wifi.config.yaml      Wi-Fi credentials - all commented out, not used (BLE does not need Wi-Fi)
//    .vscode/launch.json   VS Code "Deploy" configuration for the Meadow extension
//
//  DEPENDENCIES
//    Meadow.Sdk (project SDK)  builds and deploys the app to a Meadow board
//    Meadow.F7                 F7FeatherV2 device: pins, analog/digital ports, serial port,
//                              and Device.BluetoothAdapter
//    Meadow.Foundation         peripheral drivers - here RgbPwmLed for the onboard LED
//    Hardware: Meadow F7 Feather V2. Bluetooth runs on its ESP32 coprocessor; if the
//    adapter is not available the BLE server is not started (serial still works).
//
//  PACKET (39 characters, the same over Bluetooth and serial)
//    ###     nnn       AAAA x 6         bbbb       CHK        \r\n
//    header  packet #  A00-A05 in mV    D02-D05    checksum   end of packet
//    checksum = sum of the characters from the packet number to the last digital bit, mod 1000
//
//  CONTROL PACKET (PC or phone -> Meadow, sets outputs D06-D09)
//    ###     bbbb             CHK        \r\n
//    header  D06 D07 D08 D09  checksum   end (required on serial, optional over Bluetooth)
//    checksum = sum of the ASCII values of the 4 state chars, e.g. "1111" -> 4 x 0x31 = 196
//
//  BLUETOOTH PROTOCOL (read by MauiMVVMbt20261018 - its Utils/BleConstants.cs must match)
//    Advertised name "MeadowSolar", service 125, characteristic 5a5a5a56-69ab-4a49-a2f2-de5b292458f3
//    ("newBTpacket": string, max 40 characters, Read | Write, no Notify).
//    The client writes to the characteristic, then reads it back: the ValueSet handler
//    puts the latest packet back into the characteristic after each write.
//    A write is either a packet request (MauiMVVMbt20261018 writes "R") or an
//    output-control packet "###bbbbCHK" (same as the serial one, \r\n optional) that
//    sets D06-D09 - see BluetoothMessage_Received.
// =====================================================================================

using System;
using System.Text;
using System.Threading.Tasks;
using Meadow;
using Meadow.Devices;
using Meadow.Foundation.Leds;
using Meadow.Gateways.Bluetooth;
using Meadow.Hardware;
using Meadow.Peripherals.Leds;
using Meadow.Units;

namespace meadowBTport
{
    public class MeadowApp : App<F7FeatherV2>
    {
        // [BLUETOOTH] GATT server definition and the characteristic that holds the latest packet
        Definition bleTreeDefinition;
        CharacteristicString packetCharacteristic;

        readonly string packetCharcteristicName = "5a5a5a5669ab4a49a2f2de5b292458f3";

        // [LED]
        RgbPwmLed onboardLed;                          //onboard RGB LED, toggled red/green as a "packet sent" heartbeat
        
        bool LEDisRed = false;                         //tracks heartbeat LED color so we can alternate it

        // [SERIAL] COM1 UART to the PC
        ISerialPort serialPort;
        // [ANALOG] latest reading of each input as a 4-digit millivolt string, e.g. "1650"
        string milliVolts00 = "0000", milliVolts01 = "0000", milliVolts02 = "0000",
               milliVolts03 = "0000", milliVolts04 = "0000", milliVolts05 = "0000";

        // [ANALOG] analog input ports A00-A05
        IObservableAnalogInputPort analogIn00;
        IObservableAnalogInputPort analogIn01;
        IObservableAnalogInputPort analogIn02;
        IObservableAnalogInputPort analogIn03;
        IObservableAnalogInputPort analogIn04;
        IObservableAnalogInputPort analogIn05;

        // [DIGITAL] D02-D05 inputs (sent in the packet), D06-D09 outputs (set by the serial or Bluetooth control packet)
        IDigitalInputPort inputPortD02;
        IDigitalInputPort inputPortD03;
        IDigitalInputPort inputPortD04;
        IDigitalInputPort inputPortD05;
        IDigitalOutputPort outputPortD06;
        IDigitalOutputPort outputPortD07;
        IDigitalOutputPort outputPortD08;
        IDigitalOutputPort outputPortD09;

        // [PACKET] packet builder state used by SendLoop
        private int packetNumber;
        private int analogPin;
        private int digitalPin;
        private int packetLen;
        private int analogReq = 6;// set to the Max number of analog pins required
        private int digitalInReq = 8;// set to the Max number of digital input pins required
        private int checkSum = 0;
        private int index = 0;
        private int TXstate = 0;
        private String outPacket = "###P##AN00AN01AN02AN03AN04AN05bbbbCHKrn";
        // [BLUETOOTH] copy of the last complete packet, served by the characteristic
        private String outBTpacket = "###P##AN00AN01AN02AN03AN04AN05bbbbCHKrn";
        private String inPacket;
        private int packetTime = 100;
        // [BLUETOOTH] set when a new packet is ready (not currently used to gate the reply)
        private bool sendBTpacket = true;

        public override async Task Initialize()
        {
            Resolver.Log.Info("Initialize hardware...");

            // ---------- [LED] ----------
            //set up the onboard RGB LED (common-anode wiring on the F7 Feather)
            onboardLed = new RgbPwmLed(
                redPwmPin: Device.Pins.OnboardLedRed,
                greenPwmPin: Device.Pins.OnboardLedGreen,
                bluePwmPin: Device.Pins.OnboardLedBlue,
                CommonType.CommonAnode);

            // ---------- [BLUETOOTH] build the GATT tree and start the BLE server (needs the ESP32 coprocessor) ----------
            bleTreeDefinition = GetDefinition();
            if (Device.BluetoothAdapter == null)
            {
                Resolver.Log.Error("Bluetooth adapter (ESP32 coprocessor) not available.");
            }
            else
            {
                Device.BluetoothAdapter.StartBluetoothServer(bleTreeDefinition);
            }
            // [BLUETOOTH] reply to every client write with the latest packet,
            // [RECEIVE] and check each write for a control packet
            packetCharacteristic.ValueSet += PacketCharacteristic_ValueSet;

            //onboardLed = new RgbPwmLed(
            //    redPwmPin: Device.Pins.OnboardLedRed,
            //    greenPwmPin: Device.Pins.OnboardLedGreen,
            //    bluePwmPin: Device.Pins.OnboardLedBlue,
            //    CommonType.CommonAnode);
            inPacket = "";

            // ---------- [ANALOG] and [DIGITAL] ports ----------
            // 5 samples averaged, 40 ms apart, 3.3 V reference (same as the original defaults)
            analogIn00 = Device.CreateAnalogInputPort(Device.Pins.A00, 5);
            analogIn01 = Device.CreateAnalogInputPort(Device.Pins.A01, 5);
            analogIn02 = Device.CreateAnalogInputPort(Device.Pins.A02, 5);
            analogIn03 = Device.CreateAnalogInputPort(Device.Pins.A03, 5);
            analogIn04 = Device.CreateAnalogInputPort(Device.Pins.A04, 5);
            analogIn05 = Device.CreateAnalogInputPort(Device.Pins.A05, 5);
            inputPortD02 = Device.CreateDigitalInputPort(Device.Pins.D02, ResistorMode.Disabled);
            inputPortD03 = Device.CreateDigitalInputPort(Device.Pins.D03, ResistorMode.Disabled);
            inputPortD04 = Device.CreateDigitalInputPort(Device.Pins.D04, ResistorMode.Disabled);
            inputPortD05 = Device.CreateDigitalInputPort(Device.Pins.D05, ResistorMode.Disabled);
            outputPortD06 = Device.CreateDigitalOutputPort(Device.Pins.D06, false);
            outputPortD07 = Device.CreateDigitalOutputPort(Device.Pins.D07, false);
            outputPortD08 = Device.CreateDigitalOutputPort(Device.Pins.D08, false);
            outputPortD09 = Device.CreateDigitalOutputPort(Device.Pins.D09, false);
            // [DIGITAL] startup test: turn the outputs on one at a time
            await Task.Delay(200);
            outputPortD06.State = true;
            await Task.Delay(200);
            outputPortD08.State = true;
            await Task.Delay(200);
            outputPortD07.State = true;
            await Task.Delay(200);
            outputPortD09.State = true;

            // ---------- [SERIAL] COM1 UART at 115200 baud ----------
            serialPort = Device.CreateSerialPort(Device.PlatformOS.GetSerialPortName("COM1"), 115200);
            serialPort.Open();
            serialPort.DataReceived += SerialPort_DataReceived;

            // ---------- [ANALOG] read every 100 ms; the Updated handlers store the latest millivolts ----------
            analogIn00.Updated += AnalogIn00_Updated;
            analogIn01.Updated += AnalogIn01_Updated;
            analogIn02.Updated += AnalogIn02_Updated;
            analogIn03.Updated += AnalogIn03_Updated;
            analogIn04.Updated += AnalogIn04_Updated;
            analogIn05.Updated += AnalogIn05_Updated;
            int timeSpanInMilliSec = 100;
            analogIn00.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));
            analogIn01.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));
            analogIn02.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));
            analogIn03.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));
            analogIn04.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));
            analogIn05.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));

            await base.Initialize();
        }

        // [PACKET] Run starts the send loop, which never returns
        public override Task Run()
        {
            Resolver.Log.Info("Run...");

            return SendLoop();
        }

        // [BLUETOOTH] Called when a client (MauiMVVMbt20261018) writes to the characteristic.
        // The write replaced the value with the client's data, so put the latest packet back
        // for the client's next read, then check whether the write was an output-control packet.
        private void PacketCharacteristic_ValueSet(ICharacteristic c, object data)
        {
            // [RECEIVE] a control packet sets D06-D09; anything else (the "R" request) is ignored
            BluetoothMessage_Received(BluetoothDataToString(data));
            //if (sendBTpacket)
            {
                packetCharacteristic.SetValue(outBTpacket);
                sendBTpacket = false;
            }

        }

        // [BLUETOOTH] [RECEIVE] BluetoothMessage_Received - RECEIVE side over BLE.
        // The Bluetooth version of serialMessagePort_DataReceived (MeadowComPortV20241024):
        // the same control packet, written to the "newBTpacket" characteristic.
        //
        //   index:  0  1  2  3  4  5  6  7  8  9  10 11
        //   char :  #  #  #  b  b  b  b  C  C  C  \r \n
        //                    |  |  |  |  checksum digits
        //                    |  |  |  +-- D09 state ('0'/'1')
        //                    |  |  +----- D08 state
        //                    |  +-------- D07 state
        //                    +----------- D06 state
        //
        // Checksum = sum of the ASCII values of the 4 state chars
        // (e.g. "1010" -> 0x31+0x30+0x31+0x30 = 194 -> "194").
        // \r\n is optional: a BLE write arrives as one complete value, so no delimiter
        // is needed to find the end of the message (the serial port needs one).
        // Anything else, such as the "R" packet request, is ignored.
        // The outputs change only when the header matches AND the checksums agree.
        private void BluetoothMessage_Received(string RXmessage)
        {
            // need at least ### + 4 states + 3 checksum digits = 10 chars;
            // this length check also stops a short write from crashing the app
            if (RXmessage == null || RXmessage.Length < 10 || !RXmessage.StartsWith("###"))
            {
                return;
            }

            Resolver.Log.Info($"BT RX: {RXmessage.TrimEnd('\r', '\n')}");   //watch traffic in the Meadow console
            byte[] response = Encoding.UTF8.GetBytes(RXmessage);        //bytes so we can index individual characters

            int calChkSum = 0;                        //checksum we calculate from the 4 state chars
            for (int i = 3; i < 7; i++)
            {
                calChkSum += response[i];
            }
            //convert the 3 ASCII checksum digits (chars 7,8,9) to an integer
            //(subtracting 0x30 converts an ASCII digit to its numeric value)
            int recChkSum = (response[7] - 0x30) * 100 + (response[8] - 0x30) * 10 + (response[9] - 0x30);

            if (calChkSum == recChkSum)               //only act on the packet if the checksums agree
            {
                //'1' (0x31) turns an output on, anything else turns it off
                outputPortD06.State = response[3] == 0x31;
                outputPortD07.State = response[4] == 0x31;
                outputPortD08.State = response[5] == 0x31;
                outputPortD09.State = response[6] == 0x31;
            }
            else
            {
                Resolver.Log.Info($"BT RX checksum error: calculated {calChkSum}, received {recChkSum}");
            }
        }

        // [BLUETOOTH] [RECEIVE] a CharacteristicString delivers the written value as a string;
        // accept raw bytes too, in case a client writes a byte array
        private static string BluetoothDataToString(object data)
        {
            if (data is string text)
            {
                return text;
            }
            if (data is byte[] bytes)
            {
                return Encoding.UTF8.GetString(bytes);
            }
            return data?.ToString() ?? "";
        }

        // [BLUETOOTH] GATT tree: device "MeadowSolar" -> service 125 -> string characteristic "newBTpacket".
        // The name and UUIDs must match Utils/BleConstants.cs in MauiMVVMbt20261018.
        private Definition GetDefinition()
        {

            packetCharacteristic = new CharacteristicString(
                name: "newBTpacket",
                uuid: packetCharcteristicName,
                maxLength: 40,
                permissions: CharacteristicPermission.Read | CharacteristicPermission.Write,
                properties: CharacteristicProperty.Read | CharacteristicProperty.Write
                //permissions: CharacteristicPermission.Read,
                //properties: CharacteristicProperty.Read
                );
            var service = new Service(
                name: "ServiceA",
                uuid: 125,
                packetCharacteristic
                );
            return new Definition("MeadowSolar", service);
        }

        // [ANALOG] Updated handlers: convert each reading to a 4-digit millivolt string for the packet
        private void AnalogIn00_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Resolver.Log.Info($"{e.New.Millivolts}");
            int milliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts00 = milliVolts.ToString("D4");
            //Resolver.Log.Info(milliVolts00);

        }
        private void AnalogIn01_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts01 = miliVolts.ToString("D4");
        }
        private void AnalogIn02_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts02 = miliVolts.ToString("D4");
        }
        private void AnalogIn03_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts03 = miliVolts.ToString("D4");
        }
        private void AnalogIn04_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts04 = miliVolts.ToString("D4");
        }
        private void AnalogIn05_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts05 = miliVolts.ToString("D4");
        }


        // [SERIAL] Receives a 12-byte control packet from the PC: "###" + 4 bits ('1' = on) + 3-digit
        // checksum + \r\n, and sets outputs D06-D09 [DIGITAL]. BluetoothMessage_Received does the same over BLE.
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int calChkSum = 0;
            int recChkSum = 0;

            //Resolver.Log.Info($"{serialPort.BytesToRead}");
            byte[] response = new byte[12];
            this.serialPort.Read(response, 0, 12);
            //serialPort.Write(response);
            if (response[0] == 0x23 &&
               response[1] == 0x23 &&
               response[2] == 0x23)
            {
                for (int i = 3; i < 7; i++)
                {
                    calChkSum += response[i];
                }
                recChkSum = (response[7] - 0x30) * 100 + (response[8] - 0x30) * 10 + (response[9] - 0x30);
                if (calChkSum == recChkSum)
                {
                    outputPortD06.State = response[3] == 0x31;
                    outputPortD07.State = response[4] == 0x31;
                    outputPortD08.State = response[5] == 0x31;
                    outputPortD09.State = response[6] == 0x31;
                }
            }
        }

        // [PACKET] Builds a packet in steps (TXstate 0-3), sends it (TXstate 4),
        // waits packetTime ms, then starts over.
        private async Task SendLoop()
        {
            Resolver.Log.Info("Sending Data");

            while (true)
            {
                switch (TXstate)
                {
                    case 0: // begin making output packet
                        outPacket = "###"; //header
                        checkSum = 0;
                        index = 0;
                        analogPin = 0;
                        digitalPin = digitalInReq - 1;//start with Most significant pin
                        outPacket += packetNumber++.ToString("D3"); //inc packet number add to outPacket string
                        packetNumber %= 1000;   //packetnumber rollover code
                        TXstate = 1;  //move to next state
                        break;
                    case 1: // continue making output packet and do analog at the same time

                        outPacket += milliVolts00 + milliVolts01 + milliVolts02 + milliVolts03 + milliVolts04 + milliVolts05;

                        TXstate = 2;// move to next state when all analog complete
                        break;
                    case 2:
                        bool[] currentState = new bool[4];
                        currentState[0] = inputPortD02.State;
                        currentState[1] = inputPortD03.State;
                        currentState[2] = inputPortD04.State;
                        currentState[3] = inputPortD05.State;

                        foreach (bool state in currentState)
                        {
                            outPacket += state ? "1" : "0";
                        }
                        TXstate = 3;//move to next state when all input complete
                        break;
                    case 3:
                        for (int i = 3; i < outPacket.Length; i++)
                        {
                            checkSum += (byte)outPacket[i];//calculate check sum
                        }
                        checkSum %= 1000; //trucate check sum to 3 digits
                        outPacket += checkSum.ToString("D3");
                        outPacket += "\r\n";// add carriage return, line feed
                        packetLen = outPacket.Length;//set packet length to send
                        TXstate = 4; //move to next state
                        break;
                    case 4: // send the completed packet, then wait for the next interval
                        {
                            // [BLUETOOTH] store the packet in the characteristic so the next client read gets it
                            outBTpacket = outPacket.Substring(0, 39);
                            sendBTpacket = true;
                            packetCharacteristic.SetValue(outBTpacket);
                            Resolver.Log.Info(outBTpacket);
                            // [SERIAL] send the same packet on COM1
                            var buffer = Encoding.UTF8.GetBytes(outPacket);
                            serialPort.Write(buffer);
                            // [LED] toggle red/green so you can see packets going out
                            if (LEDisRed)
                            {
                                onboardLed.SetColor(Color.Green);
                                LEDisRed = false;
                            }
                            else
                            {
                                onboardLed.SetColor(Color.Red);
                                LEDisRed = true;
                            }
                            await Task.Delay(packetTime);

                            TXstate = 0; //reset the state when the whole packet is sent
                        }
                        break;
                }

            }
        }

    }
}

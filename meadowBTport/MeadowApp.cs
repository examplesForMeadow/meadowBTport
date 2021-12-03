using Meadow;
using Meadow.Devices;
using Meadow.Foundation;
using Meadow.Foundation.Leds;
using Meadow.Hardware;
using Meadow.Units;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meadow.Gateways.Bluetooth;

namespace meadowBTport
{
    public class MeadowApp : App<F7Micro, MeadowApp>
    {
        Definition bleTreeDefinition;
        CharacteristicString packetCharacteristic;

        readonly string packetCharcteristicName = "5a5a5a5669ab4a49a2f2de5b292458f3";

        //RgbPwmLed onboardLed;
        ISerialPort serialPort;
        string milliVolts00, milliVolts01, milliVolts02, milliVolts03, milliVolts04, milliVolts05;

        IAnalogInputPort analogIn00;
        IAnalogInputPort analogIn01;
        IAnalogInputPort analogIn02;
        IAnalogInputPort analogIn03;
        IAnalogInputPort analogIn04;
        IAnalogInputPort analogIn05;

        IDigitalInputPort inputPortD02;
        IDigitalInputPort inputPortD03;
        IDigitalInputPort inputPortD04;
        IDigitalInputPort inputPortD05;
        IDigitalOutputPort outputPortD06;
        IDigitalOutputPort outputPortD07;
        IDigitalOutputPort outputPortD08;
        IDigitalOutputPort outputPortD09;

        private int packetNumber;
        private int milliVolts;
        private int analogPin;
        private int digitalPin;
        private int packetLen;
        private int analogReq = 6;// set to the Max number of analog pins required
        private int digitalInReq = 8;// set to the Max number of digital input pins required
        private int checkSum = 0;
        private int index = 0;
        private int TXstate = 0;
        private int RXstate = 0;
        private int RXindex = 0;
        private String outPacket = "###P##AN00AN01AN02AN03AN04AN05bbbbCHKrn";
        private String outBTpacket = "###P##AN00AN01AN02AN03AN04AN05bbbbCHKrn";
        private String inPacket;
        private int packetTime = 1000;
        private bool sendBTpacket = true;

        public MeadowApp()
        {
            Initialize();
            SendLoop();
        }

        void Initialize()
        {
            Console.WriteLine("Initialize hardware...");

            bleTreeDefinition = GetDefinition();
            Device.BluetoothAdapter.StartBluetoothServer(bleTreeDefinition);
            packetCharacteristic.ValueSet += PacketCharacteristic_ValueSet;

            //onboardLed = new RgbPwmLed(device: Device,
            //    redPwmPin: Device.Pins.OnboardLedRed,
            //    greenPwmPin: Device.Pins.OnboardLedGreen,
            //    bluePwmPin: Device.Pins.OnboardLedBlue,
            //    3.3f, 3.3f, 3.3f,
            //    Meadow.Peripherals.Leds.IRgbLed.CommonType.CommonAnode);
            inPacket = "";
            analogIn00 = Device.CreateAnalogInputPort(Device.Pins.A00);
            analogIn01 = Device.CreateAnalogInputPort(Device.Pins.A01);
            analogIn02 = Device.CreateAnalogInputPort(Device.Pins.A02);
            analogIn03 = Device.CreateAnalogInputPort(Device.Pins.A03);
            analogIn04 = Device.CreateAnalogInputPort(Device.Pins.A04);
            analogIn05 = Device.CreateAnalogInputPort(Device.Pins.A05);
            inputPortD02 = Device.CreateDigitalInputPort(Device.Pins.D02);
            inputPortD03 = Device.CreateDigitalInputPort(Device.Pins.D03);
            inputPortD04 = Device.CreateDigitalInputPort(Device.Pins.D04);
            inputPortD05 = Device.CreateDigitalInputPort(Device.Pins.D05);
            outputPortD06 = Device.CreateDigitalOutputPort(Device.Pins.D06, false);
            outputPortD07 = Device.CreateDigitalOutputPort(Device.Pins.D07, false);
            outputPortD08 = Device.CreateDigitalOutputPort(Device.Pins.D08, false);
            outputPortD09 = Device.CreateDigitalOutputPort(Device.Pins.D09, false);
            Thread.Sleep(200);
            outputPortD06.State = true;
            Thread.Sleep(200);
            outputPortD08.State = true;
            Thread.Sleep(200);
            outputPortD07.State = true;
            Thread.Sleep(200);
            outputPortD09.State = true;
            serialPort = Device.CreateSerialPort(Device.SerialPortNames.Com1, 115200);
            serialPort.Open();
            serialPort.DataReceived += SerialPort_DataReceived;
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
            analogIn05.StartUpdating(TimeSpan.FromMilliseconds(timeSpanInMilliSec));

        }

        private void PacketCharacteristic_ValueSet(ICharacteristic c, object data)
        {
            //if (sendBTpacket)
            {
                packetCharacteristic.SetValue(outBTpacket);
                sendBTpacket = false;
            }

        }

        private Definition GetDefinition()
        {

            packetCharacteristic = new CharacteristicString(
                name: "newBTpacket",
                uuid: packetCharcteristicName,
                maxLength: 20,
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

        private void AnalogIn00_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Console.WriteLine(e.New.Millivolts);
            int milliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts00 = milliVolts.ToString("D4");
            //Console.WriteLine(milliVolts00);

        }
        private void AnalogIn01_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Console.WriteLine(e.New.Millivolts);
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts01 = miliVolts.ToString("D4");
        }
        private void AnalogIn02_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Console.WriteLine(e.New.Millivolts);
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts02 = miliVolts.ToString("D4");
        }
        private void AnalogIn03_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Console.WriteLine(e.New.Millivolts);
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts03 = miliVolts.ToString("D4");
        }
        private void AnalogIn04_Updated(object sender, IChangeResult<Voltage> e)
        {
            //Console.WriteLine(e.New.Millivolts);
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts04 = miliVolts.ToString("D4");
        }
        private void AnalogIn05_Updated(object sender, IChangeResult<Voltage> e)
        {
            int miliVolts = Convert.ToInt32(e.New.Millivolts);
            milliVolts05 = miliVolts.ToString("D4");
        }


        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int calChkSum = 0;
            int recChkSum = 0;

            //Console.WriteLine(serialPort.BytesToRead);
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
                //Console.WriteLine(calChkSum);
                recChkSum = (response[7] - 0x30) * 100 + (response[8] - 0x30) * 10 + (response[9] - 0x30);
                //Console.WriteLine(recChkSum);
                if (calChkSum == recChkSum)
                {
                    outputPortD06.State = response[3] == 0x31 ? true : false;
                    outputPortD07.State = response[4] == 0x31 ? true : false;
                    outputPortD08.State = response[5] == 0x31 ? true : false;
                    outputPortD09.State = response[6] == 0x31 ? true : false;
                    //Console.WriteLine(outputPortD09.State);
                }
            }
        }

        private void SendLoop()
        {
            Console.WriteLine("Sending Data");

            while (true)
            {
                switch (TXstate)
                {
                    case 0: // begin making output packet
                        //Console.WriteLine(outPacket);

                        outPacket = "###"; //header
                        checkSum = 0;
                        index = 0;
                        analogPin = 0;
                        //digitalPin=0;
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
                            string outString = " ";
                            if (state)
                            {
                                outString = "1";
                            }
                            else
                            {
                                outString = "0";
                            }
                            outPacket += outString;
                        }
                        //Console.WriteLine(outPacket);
                        //Thread.Sleep(1000);
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
                        //Console.WriteLine(outPacket);
                        //Thread.Sleep(1000);
                        TXstate = 4; //move to next state
                        break;
                    case 4: // stay in case 4 until entire packet is sent 
                        //if (index == packetLen)// when entire packet is sent check interval
                        {
                            outBTpacket = outPacket.Substring(0,10);
                            sendBTpacket = true;
                            //packetCharacteristic.SetValue(outBTpacket);
                            Console.WriteLine(outBTpacket);
                            var buffer = new byte[39];
                            buffer = Encoding.UTF8.GetBytes(outPacket);
                            serialPort.Write(buffer);
                            Thread.Sleep(packetTime);

                            TXstate = 0; //reset the state when the whole packet is sent
                        }
                        break;
                }

            }
        }

    }
}


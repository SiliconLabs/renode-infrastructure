//
// Copyright (c) 2010-2021 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.

using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class NRF52840_GPIO : BaseGPIOPort, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IDoubleWordPeripheral, IKnownSize
    {
        public NRF52840_GPIO(Machine machine) : base(machine, NumberOfPins)
        {
            Pins = new Pin[NumberOfPins];
            for(var i = 0; i < Pins.Length; i++)
            {
                Pins[i] = new Pin(this, i);
            }

            RegistersCollection = new DoubleWordRegisterCollection(this);
            physicalPinState = new IFlagRegisterField[NumberOfPins];
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();

            RegistersCollection.Reset();

            foreach(var pin in Pins)
            {
                pin.Reset();
            }

            detectState = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public override void OnGPIO(int number, bool value)
        {
            base.OnGPIO(number, value);
            if(CheckPinNumber(number))
            {
                PinChanged?.Invoke(Pins[number], value);
            }
        }

        public DoubleWordRegisterCollection RegistersCollection { get; }
        public Pin[] Pins { get; }

        public long Size => 0x1000;

        public event Action<Pin, bool> PinChanged;
        public event Action Detect;

        private void DefineRegisters()
        {
            Registers.Out.Define(this)
                .WithFlags(0, NumberOfPins, out physicalPinState,
                    writeCallback: (id, _, val) => Pins[id].Value = val)
            ;

            Registers.OutSet.Define(this)
                .WithFlags(0, NumberOfPins,
                    valueProviderCallback: (id, _) => physicalPinState[id].Value,
                    writeCallback: (id, _, val) =>
                    {
                        if(val)
                        {
                            Pins[id].Value = true;
                            physicalPinState[id].Value = true;
                        }
                    })
            ;

            Registers.OutClear.Define(this)
                .WithFlags(0, NumberOfPins,
                    valueProviderCallback: (id, _) => physicalPinState[id].Value,
                    writeCallback: (id, _, val) =>
                    {
                        if(val)
                        {
                            Pins[id].Value = false;
                            physicalPinState[id].Value = false;
                        }
                    })
            ;

            Registers.In.Define(this)
                .WithFlags(0, NumberOfPins, FieldMode.Read,
                    valueProviderCallback: (id, _) => Pins[id].Value)
            ;

            Registers.Direction.Define(this)
                .WithFlags(0, NumberOfPins,
                    valueProviderCallback: (id, _) => Pins[id].Direction == PinDirection.Output,
                    writeCallback: (id, _, val) => Pins[id].Direction = val ? PinDirection.Output : PinDirection.Input)
            ;

            Registers.DirectionSet.Define(this)
                .WithFlags(0, NumberOfPins,
                    valueProviderCallback: (id, _) => Pins[id].Direction == PinDirection.Output,
                    writeCallback: (id, _, val) => { if(val) Pins[id].Direction = PinDirection.Output; })
            ;

            Registers.DirectionClear.Define(this)
                .WithFlags(0, NumberOfPins,
                    valueProviderCallback: (id, _) => Pins[id].Direction == PinDirection.Output,
                    writeCallback: (id, _, val) => { if(val) Pins[id].Direction = PinDirection.Input; })
            ;

            Registers.Latch.Define(this)
                .WithTag("LATCH", 0, 32)
            ;

            Registers.DetectMode.Define(this)
                .WithTaggedFlag("DETECTMODE", 0)
                .WithReservedBits(1, 31)
            ;

            Registers.PinConfigure.DefineMany(this, NumberOfPins, (register, idx) =>
            {
                register
                    .WithFlag(0, name: "DIR",
                        writeCallback: (_, val) =>
                        {
                            var newValue = val ? PinDirection.Output : PinDirection.Input;
                            if(newValue != Pins[idx].Direction)
                            {
                                Pins[idx].Direction = newValue;
                                Pins[idx].Value = physicalPinState[idx].Value;
                            }
                        },
                        valueProviderCallback: _ => Pins[idx].Direction == PinDirection.Output)
                    .WithFlag(1, name: "INPUT",
                        writeCallback: (_, val) => Pins[idx].InputOverride = !val,
                        valueProviderCallback: _ => !Pins[idx].InputOverride)
                    .WithEnumField<DoubleWordRegister, PullMode>(2, 2, name: "PULL",
                        writeCallback: (_, val) =>
                        {
                            Pins[idx].PullMode = val;
                            switch(val)
                            {
                                case PullMode.PullUp:
                                    Pins[idx].RawValue = true;
                                    break;
                                case PullMode.PullDown:
                                    Pins[idx].RawValue = false;
                                    break;
                                default:
                                    break;
                            }
                        },
                        valueProviderCallback: _ => Pins[idx].PullMode)
                    .WithReservedBits(4, 4)
                    .WithEnumField<DoubleWordRegister, DriveMode>(8, 3, name: "DRIVE",
                        valueProviderCallback: _ => Pins[idx].DriveMode,
                        writeCallback: (_, val) => Pins[idx].DriveMode = val
                    )
                    .WithReservedBits(11, 5)
                    .WithEnumField<DoubleWordRegister, SenseMode>(16, 2, name: "SENSE",
                        writeCallback: (_, val) =>
                        {
                            if(Pins[idx].SenseMode != val)
                            {
                                Pins[idx].SenseMode = val;
                                UpdateDetect();
                            }
                        },
                        valueProviderCallback: _ => Pins[idx].SenseMode)
                    .WithReservedBits(18, 14)
                ;
            });
        }

        private void UpdateDetect()
        {
            var nextDetectState = Pins.Any(x => x.IsSensing);
            if(nextDetectState != detectState)
            {
                detectState = nextDetectState;
                if(nextDetectState)
                {
                    Detect?.Invoke();
                }
            }
        }

        private bool detectState;
        private IFlagRegisterField[] physicalPinState;

        private const int NumberOfPins = 32;

        public class Pin
        {
            public Pin(NRF52840_GPIO parent, int id)
            {
                this.Parent = parent;
                this.Id = id;
            }

            public void Reset()
            {
                Parent.Connections[Id].Set(false);
                Direction = PinDirection.Input;
                InputOverride = false;
            }

            public bool Value
            {
                get
                {
                    if(Direction != PinDirection.Input && !InputOverride)
                    {
                        Parent.Log(LogLevel.Noisy, "Trying to read pin #{0} that is not configured as input", Id);
                        return false;
                    }

                    switch(DriveMode)
                    {
                        case DriveMode.OpenZeroStandardOne:
                        case DriveMode.OpenZeroHighDriveOne:
                            // Open-source aka. wired-or
                            return (RawValue | PullMode == PullMode.PullUp);
                        case DriveMode.StandardZeroOpenOne:
                        case DriveMode.HighDriveZeroOpenOne:
                            // Open-drain aka. wired-and
                            return (RawValue & PullMode == PullMode.PullUp);
                        default:
                            return RawValue;
                    }
                }
                set
                {
                    if(Direction != PinDirection.Output)
                    {
                        if(value)
                        {
                            Parent.Log(LogLevel.Warning, "Trying to write pin #{0} that is not configured as output", Id);
                        }
                        return;
                    }

                    Parent.NoisyLog("Setting pin {0} to {1}", Id, value);
                    Parent.Connections[Id].Set(value);
                    Parent.State[Id] = value;
                    Parent.PinChanged(this, value);
                    Parent.UpdateDetect();
                }
            }

            // This property is needed as the pull-up and pull-down should be
            // able to override Pin state not matter Direction it is set to.
            public bool RawValue
            {
                get
                {
                    return Parent.State[Id];
                }
                set
                {
                    Parent.State[Id] = value;
                }
            }

            public PinDirection Direction { get; set; }

            public bool InputOverride { set; get; }

            public DriveMode DriveMode { set; get; }

            public SenseMode SenseMode { get; set; }

            public PullMode PullMode { get; set; }

            public bool IsSensing
            {
                get
                {
                    if(SenseMode == SenseMode.Disabled)
                    {
                        return false;
                    }

                    return (SenseMode == SenseMode.High && Value) || (SenseMode == SenseMode.Low && !Value);
                }
            }

            public NRF52840_GPIO Parent { get; }
            public int Id { get; }
        }

        public enum SenseMode
        {
            Disabled = 0,
            High = 2,
            Low = 3
        }

        public enum PullMode
        {
            Disabled = 0,
            PullDown = 1,
            PullUp = 3,
        }

        public enum DriveMode
        {
            Standard = 0,
            HighDriveZero,
            HighDriveOne,
            HighDrive,
            OpenZeroStandardOne,
            OpenZeroHighDriveOne,
            StandardZeroOpenOne,
            HighDriveZeroOpenOne
        }

        public enum PinDirection
        {
            Input,
            Output
        }

        private enum Registers
        {
            Out = 0x504,
            OutSet = 0x508,
            OutClear = 0x50C,
            In = 0x510,
            Direction = 0x514,
            DirectionSet = 0x518,
            DirectionClear = 0x51C,
            Latch = 0x520,
            DetectMode = 0x524,
            // this is a group of 32 registers for each pin
            PinConfigure = 0x700
        }
    }
}

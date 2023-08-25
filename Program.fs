namespace MeadowCCS811

open System
open System.IO
open Meadow
open Meadow.Devices
open Meadow.Hardware
open Meadow.Foundation
open Meadow.Foundation.Graphics
open Meadow.Foundation.Displays
open Meadow.Foundation.Sensors.Atmospheric
open Meadow.Foundation.Leds

type MeadowApp() =
    inherit App<F7FeatherV1>()
    
    override this.Run () =
        do Resolver.Log.Info "Run... (F#)"

        let i2c = MeadowApp.Device.CreateI2cBus(Hardware.I2cBusSpeed.Standard)
        let sensor = new Ccs811 (i2c)
        let ppm = Units.Concentration.UnitType.PartsPerMillion
        let led = RgbLed(MeadowApp.Device.Pins.OnboardLedRed, MeadowApp.Device.Pins.OnboardLedGreen,
                                MeadowApp.Device.Pins.OnboardLedBlue)
        let onboardLEDColor = RgbLedColors.Cyan

        let config = new SpiClockConfiguration(new Meadow.Units.Frequency(48.0, Meadow.Units.Frequency.UnitType.Kilohertz), 
                                    SpiClockConfiguration.Mode.Mode3)
        let spiBus = MeadowApp.Device.CreateSpiBus(MeadowApp.Device.Pins.SCK,
                                    MeadowApp.Device.Pins.MOSI,
                                    MeadowApp.Device.Pins.MISO,
                                    config)

        let display = new Gc9a01 (spiBus, 
                    MeadowApp.Device.Pins.D02, 
                    MeadowApp.Device.Pins.D01, 
                    MeadowApp.Device.Pins.D00)

        let displaywidth = Convert.ToInt32(display.Width)
        let displayheight = Convert.ToInt32(display.Height)

        let originX = displaywidth / 2
        let originY = displayheight / 2

        let LoadBmp filename = 
            let filePath = Path.Combine(MeadowOS.FileSystem.UserFileSystemRoot, $"{filename}.bmp");
            let image = Image.LoadFromFile(filePath)
            image

        let upBmpImage = LoadBmp("arrow-up")
        
        let dnBmpImage = LoadBmp("arrow-down")

        
        let triggerThreshold = Nullable (Units.Concentration(750.0, ppm))
        let reductionThreshold = Nullable (Units.Concentration(650.0, ppm))
        let nominalCO2Value = Nullable (Units.Concentration(400.0, ppm))
        let mutable latestCO2Value = Nullable (Units.Concentration(400.0, ppm))
        let mutable previousCO2Value = Nullable (Units.Concentration(0.0, ppm))
        let mutable projectedCO2Value = Nullable (Units.Concentration(400.0, ppm))
        
        let mutable canvas = MicroGraphics(display)
        
        do Resolver.Log.Info "Hello, Meadow!"

        let mutable updateDisplay = 
            async {
                let outerCircleColor = match projectedCO2Value.Value.PartsPerMillion with
                                            | i when i >= 2000.0 -> Color.Red
                                            | i when i >= 1000.0 && i < 2000.0 -> Color.DarkOrange
                                            | i when i >= 650.0 && i < 1000.0 -> Color.BurlyWood
                                            | _ -> Color.LightSteelBlue
        
                let centerCircleColor = match latestCO2Value.Value.PartsPerMillion with
                                            | i when i >= 2000.0 -> Color.Red
                                            | i when i >= 1000.0 && i < 2000.0 -> Color.DarkOrange
                                            | i when i >= 650.0 && i < 1000.0 -> Color.BurlyWood
                                            | _ -> Color.LightSteelBlue
        
                let previousValueColor = match previousCO2Value.Value.PartsPerMillion with
                                            | i when i >= 2000.0 -> Color.Red
                                            | i when i >= 1000.0 && i < 2000.0 -> Color.DarkOrange
                                            | i when i >= 650.0 && i < 1000.0 -> Color.BurlyWood
                                            | _ -> Color.LightSteelBlue
        
                let directionImage = match latestCO2Value.Value.PartsPerMillion with
                                        | i when i > previousCO2Value.Value.PartsPerMillion -> upBmpImage
                                        | _ -> dnBmpImage
        
                canvas.IgnoreOutOfBoundsPixels <- true
                canvas.CurrentFont <- Font12x16()
                canvas.Clear(false)
                canvas.DrawCircle(originX, originY, 115, outerCircleColor, true, true)
                canvas.DrawCircle(originX, originY, 90, Color.Black, true, true)
                canvas.DrawCircle(originX, originY, 80, centerCircleColor, true, true)
                canvas.DrawRoundedRectangle(48, 97, 145, 45, 8, Color.Black, true)
                canvas.DrawText(120, 98, $"{latestCO2Value}", centerCircleColor, ScaleFactor.X3, HorizontalAlignment.Center)
                canvas.DrawRoundedRectangle(62, 68, 115, 24, 6, Color.Black, true)
                canvas.DrawRoundedRectangle(62, 145, 55, 24, 6, Color.Black, true)
                canvas.DrawRoundedRectangle(120, 143, 55, 24, 6, Color.Black, true)
                canvas.DrawRoundedRectangle(102, 172, 40, 36, 8, Color.Black, true)
                canvas.CurrentFont <- Font6x8()
                canvas.DrawText(67, 73, $"Breathe", Color.LightSeaGreen, ScaleFactor.X2, HorizontalAlignment.Left)            
                canvas.DrawText(175, 73, $"EZ", Color.DeepPink, ScaleFactor.X2, HorizontalAlignment.Right)
                canvas.DrawText(115, 150, $"{previousCO2Value}", previousValueColor, ScaleFactor.X2, HorizontalAlignment.Right)
                canvas.DrawText(172, 150, $"{projectedCO2Value}", outerCircleColor, ScaleFactor.X2, HorizontalAlignment.Right)
                canvas.DrawImage (104, 174, directionImage)
                canvas.Show()
            }
        
        
        let mutable relayOne = Relays.Relay(MeadowApp.Device.Pins.D05)
        let mutable ventilationIsOn = false
        
        let toggleRelay duration =
            async {
                Resolver.Log.Info "Ventilator ON..."
                while latestCO2Value.Value.PartsPerMillion > reductionThreshold.Value.PartsPerMillion do
                    ventilationIsOn <- true
                    if not relayOne.IsOn then 
                        relayOne.Toggle()
                    if not led.IsOn then
                        led.SetColor(onboardLEDColor)
                    do! Async.Sleep(int (duration))
                ventilationIsOn <- false
                relayOne.Toggle()
                led.IsOn <- false
                Resolver.Log.Info "Ventilator OFF..." |> ignore
            }
        
        let consumer = Ccs811.CreateObserver(fun result ->
            let newValue = match result.New with | (co2, _) -> co2
            latestCO2Value <- newValue
            let oldValue = match result.Old.Value with | (co2 , _) -> co2
            if oldValue.HasValue then
                previousCO2Value <- oldValue
                let projectedValue = match previousCO2Value.Value.PartsPerMillion with 
                                        | i when i = 0.0 -> nominalCO2Value
                                        | _ -> Nullable (Units.Concentration((latestCO2Value.Value.PartsPerMillion + 
                                                                                (latestCO2Value.Value.PartsPerMillion - previousCO2Value.Value.PartsPerMillion)), 
                                                                                ppm))
        
                projectedCO2Value <- Nullable (Units.Concentration(Math.Max(projectedValue.Value.PartsPerMillion, nominalCO2Value.Value.PartsPerMillion), 
                                                                    ppm))
        
            if previousCO2Value.Value.PartsPerMillion <> latestCO2Value.Value.PartsPerMillion then
                updateDisplay |> Async.RunSynchronously |> ignore 
                Resolver.Log.Info $"New CO2 value: {latestCO2Value}" |> ignore
            if newValue.Value.PartsPerMillion > triggerThreshold.Value.PartsPerMillion && not ventilationIsOn then 
                toggleRelay 2000 |> Async.Start |> ignore)
        
        do sensor.StartUpdating(TimeSpan.FromSeconds(2.0))
        let mutable s = sensor.Subscribe(consumer)

        base.Run()
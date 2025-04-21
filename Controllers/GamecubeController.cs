﻿using Delfinovin.Controls;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System;

namespace Delfinovin.Controllers
{
    public class GamecubeController
    {
        private IXbox360Controller _controller;
        private ControllerStatus _previousInput;

        public ConnectionStatus ConnectionStatus { get; private set; }
        public CalibrationStatus CalibrationStatus { get; set; }
        public ControllerType ControllerType { get; set; }

        public GamecubeCalibration Calibration { get; set; }
        public bool IsPlayingRecording { get; private set; }
        public bool IsVibrating { get; set; }
        public int ControllerPort { get; private set; }
        public int CalibrationAttempt;

        public GamecubeController(ViGEmClient client, int port)
        {
            // Create an Xbox controller from the client provided
            _controller = client.CreateXbox360Controller();

            // Save the controller port this controller is assigned to
            ControllerPort = port;

            // Initialize our values
            Init();
        }

        private void Init()
        {
            // We want to submit our reports manually,
            // set this to false
            _controller.AutoSubmitReport = false;
        }

        public void Connect(ControllerType type)
        {
            // Subscribe to our rumble event
            _controller.FeedbackReceived += FeedbackReceived;

            // Set the calibration to a new, default one. 
            Calibration = new GamecubeCalibration();

            // Our controller is uncalibrated currently
            CalibrationStatus = CalibrationStatus.Uncalibrated;

            // Update the type of controller we want to set this as
            ControllerType |= type;

            // Connect/turn on the controller.
            if (ConnectionStatus != ConnectionStatus.Connected)
                _controller.Connect();

            // Everything is set - set this to connected
            ConnectionStatus = ConnectionStatus.Connected;
        }

        public void Disconnect()
        {
            // If we're not connected, this shouldn't fire.
            if (ConnectionStatus != ConnectionStatus.Connected)
                return;

            // Unsubscribe from the rumble event.
            _controller.FeedbackReceived -= FeedbackReceived;

            IsVibrating = false;
            IsPlayingRecording = false;

            // Reset our controller type and current input
            ControllerType = ControllerType.None;
            UpdateInputs(new ControllerStatus());

            Calibration = new GamecubeCalibration();
            CalibrationStatus = CalibrationStatus.Uncalibrated;

            // Disconnect the Xbox controller.
            _controller.Disconnect();

            // We've disconnected the controller, set this to Disconnected.
            ConnectionStatus = ConnectionStatus.Disconnected;
        }

        public async Task PlayRecording(List<InputRecord> recording, CancellationToken token, GamecubeDialog dialog = null)
        {
            // Create a controller input to send and update the current input with one 
            ControllerStatus controllerInput = new ControllerStatus();
            UpdateInputs(controllerInput);
            IsPlayingRecording = true;

            Task task = Task.Factory.StartNew(async () =>
            {
                foreach (InputRecord record in recording)
                {
                    controllerInput.Buttons = record.ButtonsPressed;
                    controllerInput.LStick = record.LStick;
                    controllerInput.RStick = record.RStick;
                    controllerInput.Triggers = record.Triggers;

                    // If the gamecube dialog provide isn't null,
                    // update it
                    await Application.Current.Dispatcher.BeginInvoke(() => 
                    { 
                        dialog?.UpdateDialog(controllerInput); 
                    });

                    // Update the controller with the new input
                    UpdateInputs(controllerInput);

                    // Wait for this input's held time before progressing
                    await Task.Delay(record.TimePressed);

                    if (token.IsCancellationRequested)
                        break;
                }
            }, token).Result;

            await Task.WhenAll(task).ContinueWith(t =>
            {
                IsPlayingRecording = false;
            }).ConfigureAwait(false);
        }

        public void UpdateInputs(ControllerStatus input)
        {
            Vector2 ClampInput(Vector2 position, Vector2 range_in_X, Vector2 range_in_Y, Vector2 range_out) {
                int DeNormalize(float axis) { return (int)(axis * short.MaxValue); }

                range_in_X.X  = DeNormalize(range_in_X.X);
                range_in_X.Y  = DeNormalize(range_in_X.Y);
                range_in_Y.X  = DeNormalize(range_in_Y.X);
                range_in_Y.Y  = DeNormalize(range_in_Y.Y);
                range_out.X   = DeNormalize(range_out.X);
                range_out.Y   = DeNormalize(range_out.Y);
                if ((position.X >= range_in_X.X && position.X <= range_in_X.Y) && (position.Y >= range_in_Y.X && position.Y <= range_in_Y.Y)) {
                    position.X = range_out.X;
                    position.Y = range_out.Y;
                }

                return position;
            }

            // Our new ShapeStick that uses a for loop and math:
            Vector2 ShapeStick(Vector2 stick, float radRange, float lenRange)
            {
                // Process the four directions in order: Up, Right, Down, Left.
                // We want up to be processed first so we define the angle for up as -PI/2.
                for (int i = 0; i < 4; i++)
                {
                    // Calculate the angle for each cardinal direction:
                    // For i = 0: up (-π/2), i = 1: right (0), i = 2: down (π/2), i = 3: left (π)
                    float angle = - (float)Math.PI / 2 + i * ((float)Math.PI / 2);
                    
                    // Create a unit direction vector.
                    Vector2 d = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                    
                    Vector2 rangeX, rangeY, output;
                    if (Math.Abs(d.X) < 0.001f)
                    {
                        // Vertical direction (up or down)
                        rangeX = new Vector2(-radRange, radRange);
                        if (d.Y < 0) {
                            // Up: We want to snap if Y is between -1.0 and -lenRange.
                            rangeY = new Vector2(-1.0f, -lenRange);
                        } else {
                            // Down: Snap if Y is between lenRange and 1.0.
                            rangeY = new Vector2(lenRange, 1.0f);
                        }
                        output = new Vector2(0, d.Y);
                    }
                    else
                    {
                        // Horizontal direction (right or left)
                        rangeY = new Vector2(-radRange, radRange);
                        if (d.X > 0) {
                            // Right: Snap if X is between lenRange and 1.0.
                            rangeX = new Vector2(lenRange, 1.0f);
                        } else {
                            // Left: Snap if X is between -1.0 and -lenRange.
                            rangeX = new Vector2(-1.0f, -lenRange);
                        }
                        output = new Vector2(d.X, 0);
                    }
                    // Apply the clamp for the current direction.
                    stick = ClampInput(stick, rangeX, rangeY, output);
                }
                return stick;
            }
            
            // We can update our input if:
            // - The previous input is not the same
            // - This controller is connected
            if (!_previousInput.IsEqual(input) && this.ConnectionStatus == ConnectionStatus.Connected)
            {
                foreach (var entry in ProfileManager.CurrentProfiles[ControllerPort].ButtonMapping)
                {
                    Xbox360Property output = ControllerProfile.XboxPropertyMapping[entry.Key];
                    if (output.GetType().BaseType == typeof(Xbox360Button))
                    {
                        // Check to see if our input state has the same buttons pressed
                        // as our controller mapping has mapped.
                        _controller.SetButtonState((Xbox360Button)output, input.IsButtonPressed(entry.Value));
                    }

                    else if (output.GetType().BaseType == typeof(Xbox360Slider))
                    {
                        // Get if all of the mapped buttons are pressed in our input.
                        bool isPressed = input.IsButtonPressed(entry.Value);

                        // Convert the boolean value to an int
                        int sliderValue = (isPressed ? 255 : 0);

                        

                        // Check to see if LAnalog/RAnalog is being mapped.
                        // If so, see if the analog trigger value is lower than
                        // the current slider value. If so, use that one.
                        // After, check to see if the value is above the threshold.
                        // If so, immediately set the value to the trigger max (255).
                        if (entry.Value.HasFlag(GamecubeControllerButtons.LAnalog))
                        {
                            sliderValue = (int)(input.Triggers.X < sliderValue ? input.Triggers.X : sliderValue);
                            sliderValue = ApplyTriggerThreshold(sliderValue, ProfileManager.CurrentProfiles[ControllerPort].TriggerThreshold);
                        }

                        if (entry.Value.HasFlag(GamecubeControllerButtons.RAnalog))
                        {
                            sliderValue = (int)(input.Triggers.Y < sliderValue ? input.Triggers.Y : sliderValue);
                            sliderValue = ApplyTriggerThreshold(sliderValue, ProfileManager.CurrentProfiles[ControllerPort].TriggerThreshold);
                        }

                        // Assign our trigger value.
                        _controller.SetSliderValue((Xbox360Slider)output, (byte)sliderValue);
                    }
                }

                // Assign the stick values to a new set of vectors
                Vector2 LeftStick = new Vector2(input.LStick.X, input.LStick.Y);
                Vector2 RightStick = new Vector2(input.RStick.X, input.RStick.Y);

                // Scale the stick values from byte to short integers.
                LeftStick = Extensions.ScaleStickVector(Extensions.ClampToByte(LeftStick));
                RightStick = Extensions.ScaleStickVector(Extensions.ClampToByte(RightStick));

                // Get the current stick ranges from our profile settings.
                float leftRange = 1 + (1 - ProfileManager.CurrentProfiles[ControllerPort].LeftStickRange);
                float rightRange = 1 + (1 - ProfileManager.CurrentProfiles[ControllerPort].RightStickRange);

                // Apply our stick deadzones and ranges.
                // If the stick value exceeds the range, cut it off
                // so we have perfect circles.
                LeftStick = Extensions.ClampToCircle(Extensions.ApplyDeadzone(LeftStick,
                    ProfileManager.CurrentProfiles[ControllerPort].LeftStickDeadzone),
                    leftRange);

                RightStick = Extensions.ClampToCircle(Extensions.ApplyDeadzone(RightStick,
                    ProfileManager.CurrentProfiles[ControllerPort].RightStickDeadzone),
                    rightRange);

                LeftStick  = ShapeStick(LeftStick,  0.1f, 0.9f);
                RightStick = ShapeStick(RightStick, 0.1f, 0.9f);
                // Get whether or not the user wants to swap the control sticks.
                bool swapSticks = ProfileManager.CurrentProfiles[ControllerPort].SwapControlSticks;

                // Set the axis values.
                _controller.SetAxisValue(Xbox360Axis.LeftThumbX, (short)(!swapSticks ? LeftStick.X : RightStick.X));
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, (short)(!swapSticks ? LeftStick.Y : RightStick.Y));
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, (short)(!swapSticks ? RightStick.X : LeftStick.X));
                _controller.SetAxisValue(Xbox360Axis.RightThumbY, (short)(!swapSticks ? RightStick.Y : LeftStick.Y));

                // Update our controller with the new report.
                _controller.SubmitReport();

                // Update the previous input with the new one.
                _previousInput = input;
            }
        }

        private int ApplyTriggerThreshold(int triggerValue, float triggerThreshold)
        {
            return (255f * triggerThreshold) < triggerValue ? 255 : triggerValue;
        }

        private void FeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            // If neither of the motors are set
            // this means we're not vibrating.
            IsVibrating = (e.LargeMotor + e.SmallMotor) > 0;
        }
    }
}

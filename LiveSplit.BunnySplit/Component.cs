using System.Windows.Forms;
using System.Xml;

using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using System.IO.Pipes;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Collections.Generic;

namespace LiveSplit.BunnySplit
{
    class Component : LogicComponent
    {
        public override string ComponentName => "BunnySplit";

        private const string PIPE_NAME = "BunnymodXT-BunnySplit";
        private enum MessageType : byte
        {
            Time = 0x00,
            Event = 0x04
        }
        private enum EventType : byte
        {
            GameEnd = 0x00,
            MapChange = 0x01,
            TimerReset = 0x02,
            TimerStart = 0x03,
            BS_ALeapOfFaith = 0x04
        }

        private interface IEvent
        {
            TimeSpan Time { get; }
        }
        private struct GameEndEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct MapChangeEvent : IEvent
        {
            public TimeSpan Time { get; set; }
            public string Map { get; set; }
        }
        private struct TimerResetEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct TimerStartEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }
        private struct BS_ALeapOfFaithEvent : IEvent
        {
            public TimeSpan Time { get; set; }
        }

        private ComponentSettings settings = new ComponentSettings();
        private NamedPipeClientStream pipe;
        private object pipeLock = new object();
        private LiveSplitState state;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private Thread pipeThread;
        private TimerModel model;
        private TimeSpan currentTime = new TimeSpan();
        private object currentTimeLock = new object();
        private List<IEvent> events = new List<IEvent>();
        private object eventsLock = new object();
        private HashSet<string> visitedMaps = new HashSet<string>();
        private bool splitOnBSALeapOfFaith = false;

        private DateTime lastTime = DateTime.Now;

        public Component(LiveSplitState state)
        {
            this.state = state;
            state.OnStart += OnStart;
            model = new TimerModel() { CurrentState = state };
            model.InitializeGameTime();
            pipeThread = new Thread(PipeThreadFunc) { IsBackground = true, Name = "BunnySplit pipe" };
            pipeThread.Start();
        }

        public override void Dispose()
        {
            state.OnStart -= OnStart;
            cts.Cancel();

            // BXT only writes while its timer runs, so a read can stay blocked for as long as the
            // game sits in the menu, for example. Waiting for the pipe thread to finish that read
            // would freeze LiveSplit whenever the component is disposed, like when hitting cancel
            // on the Edit splits window. Closing the pipe here cancels the read so the thread exits
            // right away and LiveSplit doesn't hang.
            lock (pipeLock)
            {
                pipe?.Dispose();
            }

            if (!pipeThread.Join(TimeSpan.FromSeconds(1)))
                Debug.WriteLine("The BunnySplit pipe thread did not stop in time. Leaving it to exit on its own.");
        }

        public override XmlNode GetSettings(XmlDocument document)
        {
            return settings.GetSettings(document);
        }

        public override Control GetSettingsControl(LayoutMode mode)
        {
            return settings;
        }

        public override void SetSettings(XmlNode settings)
        {
            this.settings.SetSettings(settings);
        }

        public override void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
        {
            state.IsGameTimePaused = true;
            
            bool start = false;
            lock (eventsLock)
            {
                foreach (var ev in events)
                {
                    if (ev is GameEndEvent)
                    {
                        if (state.CurrentPhase == TimerPhase.Running && settings.ShouldSplitOnGameEnd())
                        {
                            state.SetGameTime(ev.Time);
                            model.Split();
                        }
                    }
                    else if (ev is MapChangeEvent)
                    {
                        var e = (MapChangeEvent)ev;
                        if (visitedMaps.Add(e.Map) && settings.ShouldSplitOn(e.Map))
                        {
                            state.SetGameTime(e.Time);
                            model.Split();
                        }
                    }
                    else if (ev is TimerResetEvent)
                    {
                        if (settings.IsAutoResetEnabled())
                        {
                            state.SetGameTime(ev.Time);
                            model.Reset();
                        }
                    }
                    else if (ev is TimerStartEvent)
                    {
                        if (settings.IsAutoStartEnabled())
                        {
                            state.SetGameTime(ev.Time);
                            start = true;
                        }
                    }
                    else if (ev is BS_ALeapOfFaithEvent)
                    {
                        if (!splitOnBSALeapOfFaith)
                        {
                            state.SetGameTime(ev.Time);
                            model.Split();
                            splitOnBSALeapOfFaith = true;
                        }
                    }
                }
                events.Clear();
            }
            if (start)
                model.Start();

            TimeSpan curTime;
            lock (currentTimeLock)
            {
                curTime = currentTime;
            }
            state.SetGameTime(curTime);
        }

        private void OnStart(object sender, EventArgs e)
        {
            lock (eventsLock)
            {
                events.Clear();
            }

            visitedMaps.Clear();
            splitOnBSALeapOfFaith = false;
        }

        private TimeSpan ParseTime(byte[] buf, int offset)
        {
            var hours = BitConverter.ToUInt32(buf, offset);
            var minutes = buf[offset + 4];
            var seconds = buf[offset + 5];
            var milliseconds = BitConverter.ToUInt16(buf, offset + 6);
            return new TimeSpan((int)(hours / 24), (int)(hours % 24u), minutes, seconds, milliseconds);
        }

        private void ParseMessage(byte[] buf)
        {
            switch (buf[1])
            {
                case (byte)MessageType.Time:
                    {
                        var time = ParseTime(buf, 2);
                        //Debug.WriteLine("Received time: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);

                        lock (currentTimeLock)
                        {
                            currentTime = time;
                        }
                    }
                    break;

                case (byte)MessageType.Event:
                    {
                        var eventType = buf[2];
                        var time = ParseTime(buf, 3);

                        switch (eventType)
                        {
                            case (byte)EventType.GameEnd:
                                {
                                    var ev = new GameEndEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a game end event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.MapChange:
                                {
                                    var len = BitConverter.ToInt32(buf, 11);
                                    string map = System.Text.Encoding.ASCII.GetString(buf, 15, len);

                                    var ev = new MapChangeEvent { Time = time, Map = map };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a map change event: {0}:{1}:{2}.{3}; {4}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds, map);
                                }
                                break;

                            case (byte)EventType.TimerReset:
                                {
                                    var ev = new TimerResetEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a timer reset event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.TimerStart:
                                {
                                    var ev = new TimerStartEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a timer start event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            case (byte)EventType.BS_ALeapOfFaith:
                                {
                                    var ev = new BS_ALeapOfFaithEvent() { Time = time };
                                    lock (eventsLock)
                                    {
                                        events.Add(ev);
                                    }
                                    Debug.WriteLine("Received a BS A Leap of Faith event: {0}:{1}:{2}.{3}.", time.Hours, time.Minutes, time.Seconds, time.Milliseconds);
                                }
                                break;

                            default:
                                Debug.WriteLine("Received an unknown event type: " + buf[2]);
                                break;
                        }
                    }
                    break;

                default:
                    Debug.WriteLine("Received an unknown message type: " + buf[1]);
                    break;
            }
        }

        private void PipeThreadFunc()
        {
            var buf = new byte[256];

            while (!cts.IsCancellationRequested)
            {
                // Use a new client for every connection attempt instead of reconnecting one whose pipe already broke.
                // It is asynchronous so that closing it from Dispose cancels a pending read.
                var client = new NamedPipeClientStream(
                    ".",
                    PIPE_NAME,
                    PipeAccessRights.ReadData | PipeAccessRights.WriteAttributes,
                    PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.None,
                    System.IO.HandleInheritability.None);

                Debug.WriteLine("Connecting to the pipe.");
                try
                {
                    client.Connect(0);
                    client.ReadMode = PipeTransmissionMode.Message;
                    Debug.WriteLine("Connected to the pipe.");
                }
                catch (Exception e) when (e is TimeoutException || e is IOException || e is UnauthorizedAccessException)
                {
                    client.Dispose();
                    Debug.WriteLine("Idling for 1 second.");
                    cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }

                lock (pipeLock)
                {
                    if (cts.IsCancellationRequested)
                    {
                        client.Dispose();
                        return;
                    }
                    pipe = client;
                }

                try
                {
                    ReadMessages(client, buf);
                }
                catch (Exception e)
                {
                    // Dispose closed the pipe to stop the read, so this error is expected.
                    if (cts.IsCancellationRequested)
                        return;
                    Debug.WriteLine("Error reading from the pipe: " + e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    lock (pipeLock)
                    {
                        pipe = null;
                    }
                    client.Dispose();
                }

                Debug.WriteLine("Idling for 1 second.");
                cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }

        private void ReadMessages(NamedPipeClientStream client, byte[] buf)
        {
            while (!cts.IsCancellationRequested)
            {
                var read = client.Read(buf, 0, buf.Length);

                if (read == 0)
                {
                    // The pipe was closed.
                    Debug.WriteLine("Pipe end of stream reached.");
                    return;
                }

                if (buf[0] != read)
                {
                    Debug.WriteLine("Received an incorrect number of bytes (" + read + ", expected " + buf[0] + ").");
                    continue;
                }

                ParseMessage(buf);
            }
        }
    }
}

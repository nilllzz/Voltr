using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetVoltr
{
    public delegate void ChannelMessageReceivedEvent(Channel channel, string cid, byte[] message);
    public delegate void ChannelSubscribedEvent(Channel channel, string cid);

    public sealed class Channel
    {
        private int _waitForMessages = 0;

        public event ChannelMessageReceivedEvent MessageReceived;
        public event ChannelSubscribedEvent ConnectionSubscribed;
        public event ChannelSubscribedEvent ConnectionUnsubscribed;

        public Voltr Parent { get; }
        public string Name { get; private set; }
        public ChannelState State { get; private set; } = ChannelState.Unsubscribed;

        internal Channel(Voltr parent)
            : this(parent, null)
        {
            State = ChannelState.Initial;
        }

        internal Channel(Voltr parent, string name)
        {
            Parent = parent;
            Name = name;
        }
        
        internal void ProcessServiceMessage(string message)
        {
            var op = message.Remove(message.IndexOf(' '));
            var arg = message.Remove(0, message.IndexOf(' ') + 1);
            switch (op)
            {
                case "subscribed":
                    ConnectionSubscribed?.Invoke(this, arg);
                    break;
                case "unsubscribed":
                    ConnectionUnsubscribed?.Invoke(this, arg);
                    break;
            }
        }

        internal void ReceiveMessage(byte[] message)
        {
            var separatorIndex = Array.IndexOf(message, Voltr.COLON_IDENTIFIER);

            var cIdBytes = message.Take(separatorIndex).ToArray();
            var messageBytes = message.Skip(separatorIndex + 1).ToArray();

            var cId = Encoding.ASCII.GetString(cIdBytes);

            if (_waitForMessages > 0)
                _waitForMessages--;

            MessageReceived?.Invoke(this, cId, messageBytes);
        }

        internal void CreateSucceeded(string name)
        {
            Name = name;
            State = ChannelState.Subscribed;
        }

        internal void CreateFailed()
        {
            State = ChannelState.Errored;
        }

        public async Task Subscribe()
        {
            switch (State)
            {
                case ChannelState.Initial:
                    Parent.Track(this);
                    State = ChannelState.Holding;
                    await Parent.Write("subscribe _");
                    SpinWait.SpinUntil(() => State != ChannelState.Holding);
                    if (State == ChannelState.Errored)
                        Parent.Untrack(this);
                    break;

                case ChannelState.Unsubscribed:
                    Parent.Track(this);
                    await Parent.Write("subscribe " + Name);
                    State = ChannelState.Subscribed;
                    break;

                case ChannelState.Subscribed:
                    throw new InvalidOperationException("You are already subscribed to this channel.");

                case ChannelState.Holding:
                    throw new InvalidOperationException("This channel is waiting for a response from the server and cannot be operated upon until it receives one.");

                case ChannelState.Errored:
                    throw new InvalidOperationException("This channel received an error while trying to get a name. Request a new channel from the parent.");
            }
        }

        public async Task Unsubscribe()
        {
            switch (State)
            {
                case ChannelState.Subscribed:
                    State = ChannelState.Unsubscribed;
                    Parent.Untrack(this);
                    await Parent.Write("unsubscribe " + Name);
                    break;

                case ChannelState.Holding:
                    throw new InvalidOperationException("This channel is waiting for a response from the server and cannot be operated upon until it receives one.");

                case ChannelState.Initial:
                case ChannelState.Errored:
                case ChannelState.Unsubscribed:
                    throw new InvalidOperationException("You are not subscribed to this channel.");
            }
        }

        public async Task AwaitMessages(int messages)
        {
            _waitForMessages = messages;
            await Task.Run(() =>
            {
                SpinWait.SpinUntil(() => _waitForMessages <= 0);
                _waitForMessages = 0;
            });
        }

        public async Task Publish(string message)
        {
            await Parent.Write("publish " + Name + " " + message);
        }

        public async Task Publish(byte[] message)
        {
            await Parent.Write(Encoding.ASCII.GetBytes("publish " + Name + " ").Concat(message).ToArray());
        }

        public async Task Broadcast(string message)
        {
            await Parent.Write("broadcast " + Name + " " + message);
        }

        public async Task Broadcast(byte[] message)
        {
            await Parent.Write(Encoding.ASCII.GetBytes("broadcast " + Name + " ").Concat(message).ToArray());
        }
    }
}

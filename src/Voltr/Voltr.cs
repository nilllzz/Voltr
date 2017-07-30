using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetVoltr
{
    public delegate void DirectMessageEvent(Voltr source, string cid, byte[] message);

    public sealed class Voltr
    {
        internal const byte COLON_IDENTIFIER = (byte)':';
        private const string HOST = "198.58.112.213";
        private const int PORT = 8004;

        private Encoding _encoding;
        private bool _isActive;
        private List<Channel> _activeChannels = new List<Channel>();

        internal TcpClient Client { get; private set; }

        public event DirectMessageEvent DirectMessageReceived;

        public string CId { get; private set; }
        public Encoding Encoding
        {
            get => _encoding ?? Encoding.UTF8;
            set => _encoding = value;
        }

        public async Task Open()
        {
            Client = new TcpClient();
            await Client.ConnectAsync(HOST, PORT);
            ProcessMessage(await ReadNextMessage());
            if (_isActive)
                Listen();
            else
            {
                await Close();
                throw new InvalidOperationException("The remote server returned an invalid response.");
            }
        }

        public async Task Close()
        {
            foreach (var channel in _activeChannels)
                await channel.Unsubscribe();

            _isActive = false;
            _activeChannels.Clear();
            Client.Close();
            CId = null;
        }
        
        private void Listen()
        {
            Task.Run(async () =>
            {
                while (_isActive)
                {
                    ProcessMessage(await ReadNextMessage());
                }
            });
        }

        private void ProcessMessage(IEnumerable<byte> message)
        {
            if (message.Count() > 0)
            {
                var id = message.ElementAt(0);
                switch (id)
                {
                    case (byte)'@':
                        message = message.Skip(1);
                        ProcessDirectMessage(message.ToArray());
                        break;
                    case (byte)'!':
                        message = message.Skip(1);
                        ProcessServiceMessage(message.ToArray());
                        break;
                    default:
                        ProcessChannelMessage(message.ToArray());
                        break;
                }
            }
        }

        private void ProcessChannelMessage(byte[] message)
        {
            var separatorIndex = Array.IndexOf(message, COLON_IDENTIFIER);

            var channelNameBytes = message.Take(separatorIndex).ToArray();
            var messageBytes = message.Skip(separatorIndex + 1).ToArray();

            var channelName = Encoding.ASCII.GetString(channelNameBytes);

            _activeChannels.FirstOrDefault(c => c.Name == channelName)?.ReceiveMessage(messageBytes);
        }

        private void ProcessDirectMessage(byte[] message)
        {
            var separatorIndex = Array.IndexOf(message, COLON_IDENTIFIER);

            var cidBytes = message.Take(separatorIndex).ToArray();
            var messageBytes = message.Skip(separatorIndex + 1).ToArray();

            var cid = Encoding.ASCII.GetString(cidBytes);

            DirectMessageReceived?.Invoke(this, cid, messageBytes);
        }

        private void ProcessServiceMessage(byte[] messageBytes)
        {
            var message = Encoding.ASCII.GetString(messageBytes);
            if (message.Length > 0)
            {
                // if the first char of the message is an underscore, it's a global server message
                var targetId = message[0];
                if (targetId == '_')
                {
                    ProcessGlobalServiceMessage(message.Remove(0, message.IndexOf(":") + 1));
                }
                else
                {
                    var channelName = message.Remove(message.IndexOf(":"));
                    message = message.Remove(0, message.IndexOf(":") + 1);
                    _activeChannels.FirstOrDefault(c => c.Name == channelName)?.ProcessServiceMessage(message);
                }
            }
        }

        private void ProcessGlobalServiceMessage(string message)
        {
            var op = message.Remove(message.IndexOf(' '));
            switch (op)
            {
                case "connected":
                    message = message.Remove(0, op.Length + 1);
                    ReceivedCId(message);
                    break;
                case "created":
                    message = message.Remove(0, op.Length + 1);
                    ChannelCreated(message);
                    break;
                case "createfailed":
                    ChannelCreateFailed();
                    break;
            }
        }

        private void ReceivedCId(string message)
        {
            CId = message;
            _isActive = true;
        }

        private void ChannelCreated(string message)
        {
            // assume that the id returned from the server is for the last channel that got tracked.
            _activeChannels.Last(c => c.State == ChannelState.Holding).CreateSucceeded(message);
        }

        private void ChannelCreateFailed()
        {
            _activeChannels.Last(c => c.State == ChannelState.Holding).CreateFailed();
        }

        private async Task<byte[]> ReadNextMessage()
        {
            var stream = Client.GetStream();

            using (var br = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var separator = false;
                var sb = new StringBuilder();
                while (!separator)
                {
                    var c = br.ReadChar();
                    if (char.IsNumber(c))
                        sb.Append(c);
                    else if (c == ':')
                        separator = true;
                }

                var length = int.Parse(sb.ToString());
                var buffer = new byte[length];
                var readLength = br.Read(buffer, 0, buffer.Length);
                while (readLength < length)
                {
                    await Task.Run(() => SpinWait.SpinUntil(() => stream.DataAvailable));
                    readLength += br.Read(buffer, readLength, buffer.Length - readLength);
                }

                return buffer;
            }
        }

        public Channel GetChannel()
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before requesting a channel from it.");

            return new Channel(this);
        }

        public Channel GetChannel(string channelName)
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before requesting a channel from it.");

            return _activeChannels.FirstOrDefault(c => c.Name == channelName) ?? new Channel(this, channelName);
        }

        public async Task SendDirectMessage(string cid, string message)
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before sending a direct message.");

            await Write("send @" + cid + " " + message);
        }

        public async Task SendDirectMessage(string cid, byte[] message)
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before sending a direct message.");

            await Write(Encoding.ASCII.GetBytes("send @" + cid + " ").Concat(message).ToArray());
        }

        internal async Task Write(string message)
        {
            await Write(Encoding.GetBytes(message));
        }

        internal async Task Write(byte[] message)
        {
            await Task.Run(() =>
            {
                var postBytes = Encoding.ASCII.GetBytes(message.Length + ":").Concat(message).ToArray();

                using (var br = new BinaryWriter(Client.GetStream(), Encoding.ASCII, true))
                    br.Write(postBytes);
            });
        }

        internal void Track(Channel channel)
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before subscribing to a child channel.");

            if (!_activeChannels.Contains(channel))
                _activeChannels.Add(channel);
            else
                throw new InvalidOperationException();
        }

        internal void Untrack(Channel channel)
        {
            if (!_isActive)
                throw new InvalidOperationException("You have to open the Voltr instance before unsubscribing from a child channel.");

            if (_activeChannels.Contains(channel))
                _activeChannels.Remove(channel);
            else
                throw new InvalidOperationException();
        }
    }
}

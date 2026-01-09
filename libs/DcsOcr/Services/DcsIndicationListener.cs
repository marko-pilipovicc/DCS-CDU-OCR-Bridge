using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCS.OCR.Library.Services
{
    public class DcsIndicationListener : IDisposable
    {
        private UdpClient? _udpClient;
        private readonly int _port;
        private CancellationTokenSource? _cts;
        
        public event Action<string>? IndicationReceived;

        public DcsIndicationListener(int port = 4242)
        {
            _port = port;
        }

        public bool IsRunning => _udpClient != null && _cts != null && !_cts.IsCancellationRequested;

        public void Start()
        {
            // Stop any existing listener first
            if (_udpClient != null)
            {
                Stop();
            }
            
            _cts = new CancellationTokenSource();
            
            try
            {
                // Create socket with ReuseAddress option to avoid "address already in use" errors
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
                Task.Run(() => Listen(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start DCS Indication Listener: {ex.Message}");
            }
        }

        private async Task Listen(CancellationToken token)
        {
            if (_udpClient == null) return;
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(token);
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    IndicationReceived?.Invoke(message);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving DCS indication: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient = null;
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
            _cts?.Dispose();
        }
    }
}

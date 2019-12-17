﻿using IronPython.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wirehome.Core.Cloud.Channel;
using Wirehome.Core.Cloud.Protocol;
using Wirehome.Core.Cloud.Protocol.Content;
using Wirehome.Core.Contracts;
using Wirehome.Core.Diagnostics;
using Wirehome.Core.Extensions;
using Wirehome.Core.Foundation.Model;
using Wirehome.Core.Python;
using Wirehome.Core.Storage;

namespace Wirehome.Core.Cloud
{
    public class CloudService : IService
    {
        readonly Dictionary<string, CloudMessageHandler> _rawMessageHandlers = new Dictionary<string, CloudMessageHandler>();
        readonly HttpClient _httpClient = new HttpClient();
        readonly StorageService _storageService;
        readonly CloudMessageFactory _cloudMessageFactory;
        readonly CloudMessageSerializer _cloudMessageSerializer;
                
        readonly ILogger _logger;

        CancellationTokenSource _cancellationTokenSource;
        ConnectorChannel _channel;
        bool _isConnected;

        public CloudService(StorageService storageService, SystemStatusService systemStatusService, ILogger<CloudService> logger)
        {
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (systemStatusService == null) throw new ArgumentNullException(nameof(systemStatusService));
            systemStatusService.Set("cloud.is_connected", () => _isConnected);
            systemStatusService.Set("cloud.bytes_sent", () => _channel?.GetStatistics()?.BytesSent);
            systemStatusService.Set("cloud.bytes_received", () => _channel?.GetStatistics()?.BytesReceived);
            systemStatusService.Set("cloud.connected", () => _channel?.GetStatistics()?.Connected.ToString("O"));
            systemStatusService.Set("cloud.last_message_received", () => _channel?.GetStatistics()?.LastMessageReceived?.ToString("O"));
            systemStatusService.Set("cloud.last_message_sent", () => _channel?.GetStatistics()?.LastMessageSent?.ToString("O"));
            systemStatusService.Set("cloud.messages_received", () => _channel?.GetStatistics()?.MessagesReceived);
            systemStatusService.Set("cloud.messages_sent", () => _channel?.GetStatistics()?.MessagesSent);
            systemStatusService.Set("cloud.malformed_messages_received", () => _channel?.GetStatistics()?.MalformedMessagesReceived);
            systemStatusService.Set("cloud.receive_errors", () => _channel?.GetStatistics()?.ReceiveErrors);
            systemStatusService.Set("cloud.send_errors", () => _channel?.GetStatistics()?.SendErrors);

            _httpClient.BaseAddress = new Uri("http://127.0.0.1:80");

            _cloudMessageSerializer = new CloudMessageSerializer();
            _cloudMessageFactory = new CloudMessageFactory(_cloudMessageSerializer);
        }

        public void Start()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }

            var cancellationTokenSource = new CancellationTokenSource();
            _cancellationTokenSource = cancellationTokenSource;
            Task.Run(() => ConnectAsync(cancellationTokenSource.Token), cancellationTokenSource.Token).Forget(_logger);
        }

        public void Reconnect()
        {
            _cancellationTokenSource?.Cancel();
            Start();
        }

        public void RegisterMessageHandler(string type, Func<WirehomeDictionary, WirehomeDictionary> handler)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_rawMessageHandlers)
            {
                _rawMessageHandlers[type] = new CloudMessageHandler(type, handler);
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CloudServiceOptions options = null;
                try
                {
                    _isConnected = false;

                    if (!_storageService.TryReadOrCreate(out options, CloudServiceOptions.Filename) || options == null)
                    {
                        continue;
                    }

                    if (!options.IsEnabled)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(options.IdentityUid) || string.IsNullOrEmpty(options.Password))
                    {
                        continue;
                    }

                    using (var webSocketClient = new ClientWebSocket())
                    {
                        using (var timeout = new CancellationTokenSource(options.ReconnectDelay))
                        {
                            var url = $"wss://{options.Host}/Connector";

                            webSocketClient.Options.SetRequestHeader(CustomCloudHeaderNames.IdentityUid, options.IdentityUid);
                            webSocketClient.Options.SetRequestHeader(CustomCloudHeaderNames.ChannelUid, options.ChannelUid);
                            webSocketClient.Options.SetRequestHeader(CustomCloudHeaderNames.Password, options.Password);
                            webSocketClient.Options.SetRequestHeader(CustomCloudHeaderNames.Version, WirehomeCoreVersion.Version);

                            await webSocketClient.ConnectAsync(new Uri(url, UriKind.Absolute), timeout.Token).ConfigureAwait(false);
                        }

                        _channel = new ConnectorChannel(webSocketClient, _cloudMessageSerializer, _logger);
                        _logger.LogInformation($"Connected with Wirehome.Cloud at host '{options.Host}'.");
                        _isConnected = true;

                        while (_channel.IsConnected && !cancellationToken.IsCancellationRequested)
                        {
                            var receiveResult = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                            if (receiveResult.CloseConnection || cancellationToken.IsCancellationRequested)
                            {
                                webSocketClient.Abort();
                                break;
                            }

                            if (receiveResult.Message == null)
                            {
                                continue;
                            }

                            ParallelTask.Start(() => TryProcessCloudMessageAsync(receiveResult.Message, cancellationToken), cancellationToken, _logger);
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, $"Error while connecting with Wirehome.Cloud service at host '{options?.Host}'.");
                }
                finally
                {
                    _isConnected = false;
                    _channel = null;

                    var delay = TimeSpan.FromSeconds(10);
                    if (options != null && options.ReconnectDelay > TimeSpan.Zero)
                    {
                        delay = options.ReconnectDelay;
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task TryProcessCloudMessageAsync(CloudMessage requestMessage, CancellationToken cancellationToken)
        {
            try
            {
                CloudMessage responseMessage = null;
                if (requestMessage.Type == CloudMessageType.Ping)
                {
                    responseMessage = new CloudMessage();
                }
                else if (requestMessage.Type == CloudMessageType.HttpInvoke)
                {
                    responseMessage = await InvokeHttpRequestAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                }
                else if (requestMessage.Type == CloudMessageType.Raw)
                {
                    responseMessage = InvokeRawRequest(requestMessage);
                }

                if (responseMessage != null)
                {
                    responseMessage.CorrelationId = requestMessage.CorrelationId;
                    await SendMessageAsync(responseMessage, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error while processing cloud message.");
            }
        }

        private CloudMessage InvokeRawRequest(CloudMessage requestMessage)
        {
            try
            {
                var jsonRequestPayload = Encoding.UTF8.GetString(requestMessage.Payload);
                var jsonRequest = JObject.Parse(jsonRequestPayload);
                var pythonDictionary = PythonConvert.ToPythonDictionary(jsonRequest);

                CloudMessageHandler cloudMessageHandler;
                lock (_rawMessageHandlers)
                {
                    if (!_rawMessageHandlers.TryGetValue(pythonDictionary.GetValueOr("type", string.Empty), out cloudMessageHandler))
                    {
                        throw new NotSupportedException("RAW message type handler not supported.");
                    }
                }

                var responseContent = cloudMessageHandler.Invoke(pythonDictionary);

                var jsonResponse = PythonConvert.FromPythonToJson(responseContent);

                return new CloudMessage()
                {
                    CorrelationId = requestMessage.CorrelationId,
                    Payload = Encoding.UTF8.GetBytes(jsonResponse.ToString())
                };
            }
            catch (Exception exception)
            {
                return _cloudMessageFactory.Create(exception);
            }
        }

        private async Task<CloudMessage> InvokeHttpRequestAsync(CloudMessage requestMessage, CancellationToken cancellationToken)
        {
            try
            {
                var requestContent = _cloudMessageSerializer.Unpack<HttpRequestCloudMessageContent>(requestMessage.Payload);
                
                using (var httpRequestMessage = new HttpRequestMessage())
                {
                    httpRequestMessage.Method = new HttpMethod(requestContent.Method);
                    httpRequestMessage.RequestUri = new Uri(requestContent.Uri, UriKind.Relative);

                    if (requestContent.Content?.Any() == true)
                    {
                        httpRequestMessage.Content = new ByteArrayContent(requestContent.Content);
                    }

                    if (requestContent.Headers?.Any() == true)
                    {
                        foreach (var (key, value) in requestContent.Headers)
                        {
                            if (!httpRequestMessage.Headers.TryAddWithoutValidation(key, value))
                            {
                                httpRequestMessage.Content.Headers.TryAddWithoutValidation(key, value);
                            }
                        }
                    }

                    using (var httpResponse = await _httpClient.SendAsync(httpRequestMessage, cancellationToken).ConfigureAwait(false))
                    {
                        return await _cloudMessageFactory.Create(httpResponse, requestMessage.CorrelationId).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                return _cloudMessageFactory.Create(exception);
            }
        }

        Task SendMessageAsync(CloudMessage message, CancellationToken cancellationToken)
        {
            var channel = _channel;
            if (channel == null)
            {
                return Task.CompletedTask;
            }

            return _channel.SendAsync(message, cancellationToken);
        }
    }
}

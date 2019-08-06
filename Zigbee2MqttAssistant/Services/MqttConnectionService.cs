﻿using System;
using System.Reactive.Disposables;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Client.Receiving;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Zigbee2MqttAssistant.Models.Mqtt;

namespace Zigbee2MqttAssistant.Services
{
	public class MqttConnectionService : IHostedService, IDisposable, IMqttClientConnectedHandler, IMqttClientDisconnectedHandler, IMqttApplicationMessageReceivedHandler
	{
		private readonly ILogger<MqttConnectionService> _logger;
		private readonly ISettingsService _settings;
		private readonly IBridgeStateService _stateService;

		private readonly SerialDisposable _connection = new SerialDisposable();
		private readonly SerialDisposable _devicePolling = new SerialDisposable();
		private readonly MqttFactory _mqttFactory;

		private IManagedMqttClient _client;
		private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

		public MqttConnectionService(ILogger<MqttConnectionService> _logger, ISettingsService settings, IBridgeStateService stateService)
		{
			this._logger = _logger;
			_settings = settings;
			_stateService = stateService;

			_mqttFactory = new MqttFactory();
			BuildRegexes();
		}

		private void BuildRegexes()
		{
			var settings = _settings.CurrentSettings;
			var baseTopic = $"{settings.BaseTopic}/";
			var baseHassTopic = $"{settings.HomeAssistantDiscoveryBaseTopic}/";

			var regexOptions = RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant;
			FriendlyNameExtractor = new Regex($"{Regex.Escape(baseTopic)}(?<name>[^/]+)(?:/(?<state>(availability|state)))?", regexOptions);
			HassDiscoveryExtractor = new Regex($"{Regex.Escape(baseHassTopic)}(?<class>[^/]+)/(?<deviceId>[^/]+)/(?<component>[^/]+)/(?<config>config)?", regexOptions);
		}

		public async Task StartAsync(CancellationToken ct)
		{
			_logger.LogInformation("Starting MqttConnectionService...");
			await Task.Yield();
			await Connect(ct);
			_logger.LogInformation("Subscribing to MQTT topics...");
			await Subscribe(ct);
		}

		public async Task StopAsync(CancellationToken ct)
		{
			_logger.LogInformation("Stopping MqttConnectionService...");
			await Task.Yield();
			Disconnect();
		}

		private async Task Connect(CancellationToken ct)
		{
			Disconnect();

			var settings = _settings.CurrentSettings;

			var options = new MqttClientOptionsBuilder()
				.WithTcpServer(settings.MqttServer, settings.MqttPort)
				.WithTls(x => x.UseTls = settings.MqttSecure)
				.WithCredentials(settings.MqttUsername, settings.MqttPassword)
				.Build();

			var managedOptions = new ManagedMqttClientOptionsBuilder()
				.WithClientOptions(options)
				.WithAutoReconnectDelay(TimeSpan.FromSeconds(10))
				.Build();

			_client = _mqttFactory.CreateManagedMqttClient();

			_client.ConnectedHandler = this;
			_client.DisconnectedHandler = this;
			_client.ApplicationMessageReceivedHandler = this;
			await _client.StartAsync(managedOptions);
		}

		private void StartPolling()
		{
			var cancellableDisposable = new CancellationDisposable();
			_devicePolling.Disposable = cancellableDisposable;

			var ct = cancellableDisposable.Token;

			var t = PollingTask(); // start it

			async Task PollingTask()
			{
				var settings = _settings.CurrentSettings;

				long count = 0;

				while (!ct.IsCancellationRequested)
				{
					var msg = new MqttApplicationMessageBuilder()
						.WithTopic($"{settings.BaseTopic}/bridge/config/devices/get")
						.Build();

					await _client.PublishAsync(msg);

					if (count % 3 == 0)
					{
						var msg2 = new MqttApplicationMessageBuilder()
							.WithTopic($"{settings.BaseTopic}/bridge/networkmap")
							.WithPayload("raw")
							.Build();

						await _client.PublishAsync(msg2);
					}

					await Task.Delay(TimeSpan.FromMinutes(5), ct);
				}
			}
		}

		private void StopPolling()
		{
			_devicePolling.Disposable = null;
		}

		private void Disconnect()
		{
			_connection.Disposable = null;
		}

		private async Task Subscribe(CancellationToken ct)
		{
			var settings = _settings.CurrentSettings;
			await _client.SubscribeAsync($"{settings.BaseTopic}/#");
			await _client.SubscribeAsync($"{settings.HomeAssistantDiscoveryBaseTopic}/#");
		}

		public void Dispose()
		{
			_devicePolling.Dispose();
			_connection.Dispose();
		}

		public async Task HandleApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
		{
			var msg = eventArgs.ApplicationMessage;

			if (DispatchZigbee2MqttMessage(msg))
			{
				return;
			}

			if (DispatchHassDiscoveryMessage(msg))
			{
				return;
			}

			if (DispatchDevicesMessage(msg))
			{
				return;
			}

			_logger.LogWarning($"Unable to quality a message received on topic '{msg.Topic}'.");
		}

		private Regex FriendlyNameExtractor;

		private bool DispatchZigbee2MqttMessage(MqttApplicationMessage msg)
		{
			var match = FriendlyNameExtractor.Match(msg.Topic);

			if (!match.Success)
			{
				return false;
			}

			var friendlyName = match.Groups["name"].Value;

			if (friendlyName.Equals("bridge"))
			{
				var stateGroup = match.Groups["state"];
				if (stateGroup.Success)
				{
					var value = stateGroup.Value;
					var payload = _utf8.GetString(msg.Payload);
					if (value.Equals("state"))
					{
						_stateService.SetBridgeState(isOnline: payload.Equals("online"));
					}
					else if (value.Equals("config"))
					{
						_stateService.SetBridgeConfig(configJson: payload);
					}
					return true;
				}
				return false;
			}

			if (msg.Payload == null)
			{
				return true;
			}
			else if (match.Groups["state"].Success)
			{
				var payload = _utf8.GetString(msg.Payload);
				_stateService.SetDeviceAvailability(friendlyName, payload.Equals("online"));
			}
			else
			{
				var payload = _utf8.GetString(msg.Payload);
				_stateService.UpdateDevice(friendlyName: friendlyName, payload);
			}

			return true;
		}

		private Regex HassDiscoveryExtractor;

		private bool DispatchHassDiscoveryMessage(MqttApplicationMessage msg)
		{
			var match = HassDiscoveryExtractor.Match(msg.Topic);

			if (!match.Success)
			{
				return false;
			}

			var deviceClass = match.Groups["class"];
			var id = match.Groups["deviceId"];
			var component = match.Groups["component"];
			var config = match.Groups["config"];

			if (config.Success)
			{
				var payload = _utf8.GetString(msg.Payload);
				
				_stateService.SetDeviceEntity(
					zigbeeId: id.Value,
					entityClass: deviceClass.Value,
					component: component.Value,
					configPayload: payload,
					FriendlyNameFromTopic);
			}

			string FriendlyNameFromTopic(string topic)
			{
				var topicMatch = FriendlyNameExtractor.Match(topic);
				return topicMatch.Success ? topicMatch.Groups["name"].Value : null;
			}

			return true;
		}

		private bool DispatchDevicesMessage(MqttApplicationMessage msg)
		{
			var settings = _settings.CurrentSettings;
			if (msg.Topic.Equals($"{settings.BaseTopic}/bridge/config/devices"))
			{
				var payload = _utf8.GetString(msg.Payload);
				_stateService.UpdateDevices(payload);

				return true;
			}
			if (msg.Topic.Equals($"{settings.BaseTopic}/bridge/networkmap/raw"))
			{
				var payload = _utf8.GetString(msg.Payload);
				_stateService.UpdateNetworkMap(payload);

				return true;
			}

			return false;
		}

		public async Task HandleConnectedAsync(MqttClientConnectedEventArgs eventArgs)
		{
			_logger.LogInformation($"Successfully connected to MQTT server {_settings.CurrentSettings.MqttServer}.");

			_stateService.Clear();
			disconnectWarned = false;

			StartPolling();
		}

		private bool disconnectWarned = false;

		public async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
		{
			StopPolling();

			if (disconnectWarned)
			{
				_logger.LogDebug(eventArgs.Exception, $"Error connecting to MQTT server {_settings.CurrentSettings.MqttServer}.");
				return;
			}

			disconnectWarned = true;
			if (eventArgs.ClientWasConnected)
			{
				_logger.LogWarning(eventArgs.Exception, $"Disconnected from MQTT server {_settings.CurrentSettings.MqttServer}.");
			}
			else
			{
				_logger.LogWarning(eventArgs.Exception, $"Unable to connect to MQTT server {_settings.CurrentSettings.MqttServer}.");
			}
		}
	}
}
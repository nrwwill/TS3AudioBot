// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TS3AudioBot.Algorithm;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.Text;
using TS3AudioBot.Config;
using TS3AudioBot.Dependency;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.History;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Sessions;
using TSLib;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Messages;
using TSLib.Scheduler;

namespace TS3AudioBot
{
	/// <summary>Core class managing all bots and utility modules.</summary>
	public sealed class Bot
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly ConfBot config;
		private readonly TickWorker idleTickWorker;

		internal bool IsDisposed { get; private set; }
		internal BotInjector Injector { get; }
		internal DedicatedTaskScheduler Scheduler { get; }

		public Id Id { get; }
		/// <summary>This is the template name. Can be null.</summary>
		public string? Name => config.Name;
		public bool QuizMode { get; set; }

		private readonly ResolveContext resourceResolver;
		private readonly Ts3Client ts3client;
		private readonly TsFullClient ts3FullClient;
		private readonly SessionManager sessionManager;
		private readonly PlayManager playManager;
		private readonly IVoiceTarget targetManager;
		private readonly Player player;
		private readonly Stats stats;

		public Bot(Id id, ConfBot config, BotInjector injector)
		{
			this.Id = id;
			this.config = config;
			this.Injector = injector;

			// Registering config changes
			config.Language.Changed += async (s, e) =>
			{
				var langResult = await LocalizationManager.LoadLanguage(e.NewValue, true);
				if (!langResult.Ok)
					Log.Error("Failed to load language file ({0})", langResult.Error);
			};
			config.Events.IdleDelay.Changed += (s, e) => EnableIdleTickWorker();
			config.Events.OnIdle.Changed += (s, e) => EnableIdleTickWorker();

			var builder = new DependencyBuilder(Injector);
			builder.AddModule(this);
			builder.AddModule(config);
			builder.AddModule(Injector);
			builder.AddModule(config.Playlists);
			builder.RequestModule<PlaylistIO>();
			builder.RequestModule<PlaylistManager>();
			builder.AddModule(Id);
			builder.RequestModule<DedicatedTaskScheduler>();
			builder.RequestModule<TaskScheduler, DedicatedTaskScheduler>();
			builder.RequestModule<TsFullClient>();
			builder.RequestModule<TsBaseFunctions, TsFullClient>();
			builder.RequestModule<Ts3Client>();
			builder.RequestModule<Player>();
			builder.RequestModule<CustomTargetPipe>();
			builder.RequestModule<IVoiceTarget, CustomTargetPipe>();
			builder.RequestModule<SessionManager>();
			builder.RequestModule<ResolveContext>();
			if (!builder.TryCreate<CommandManager>(out var commandManager))
				throw new Exception("Failed to create commandManager");
			builder.AddModule(commandManager);
			if (config.History.Enabled)
			{
				builder.AddModule(config.History);
				builder.RequestModule<HistoryManager>();
			}
			builder.RequestModule<PlayManager>();

			if (!builder.Build())
			{
				Log.Error("Missing bot module dependency");
				throw new Exception("Could not load all bot modules");
			}

			resourceResolver = Injector.GetModuleOrThrow<ResolveContext>();
			ts3FullClient = Injector.GetModuleOrThrow<TsFullClient>();
			ts3client = Injector.GetModuleOrThrow<Ts3Client>();
			player = Injector.GetModuleOrThrow<Player>();
			Scheduler = Injector.GetModuleOrThrow<DedicatedTaskScheduler>();
			var customTarget = Injector.GetModuleOrThrow<CustomTargetPipe>();
			player.SetTarget(customTarget);
			Injector.AddModule(ts3FullClient.Book);
			playManager = Injector.GetModuleOrThrow<PlayManager>();
			targetManager = Injector.GetModuleOrThrow<IVoiceTarget>();
			sessionManager = Injector.GetModuleOrThrow<SessionManager>();
			stats = Injector.GetModuleOrThrow<Stats>();

			idleTickWorker = Scheduler.Invoke(() => Scheduler.CreateTimer(OnIdle, TimeSpan.MaxValue, false)).Result;

			player.OnSongEnd += playManager.SongStoppedEvent;
			player.OnSongUpdated += (s, e) => playManager.Update(e);
			// Update idle status events
			playManager.BeforeResourceStarted += (s, e) => DisableIdleTickWorker();
			playManager.PlaybackStopped += (s, e) => EnableIdleTickWorker();
			// Used for the voice_mode script
			playManager.BeforeResourceStarted += BeforeResourceStarted;
			// Update the own status text to the current song title
			playManager.AfterResourceStarted += (s, e) => UpdateBotStatus();
			playManager.PlaybackStopped += (s, e) => UpdateBotStatus();
			playManager.OnResourceUpdated += (s, e) => UpdateBotStatus();
			// Log our resource in the history
			if (Injector.TryGet<HistoryManager>(out var historyManager))
				playManager.AfterResourceStarted += (s, e) =>
				{
					if (e.MetaData != null)
						historyManager.LogAudioResource(new HistorySaveData(e.PlayResource.AudioResource, e.MetaData.ResourceOwnerUid));
				};
			// Update our thumbnail
			playManager.AfterResourceStarted += (s, e) => GenerateStatusImage(true, e);
			playManager.PlaybackStopped += (s, e) => GenerateStatusImage(false, null);
			// Stats
			playManager.AfterResourceStarted += (s, e) => stats.TrackSongStart(Id, e.ResourceData.AudioType);
			playManager.ResourceStopped += (s, e) => stats.TrackSongStop(Id);
			// Register callback for all messages happening
			ts3client.OnMessageReceived += OnMessageReceived;
			// Register callback to remove open private sessions, when user disconnects
			ts3FullClient.OnEachClientLeftView += OnClientLeftView;
			ts3client.OnBotDisconnect += OnBotDisconnect;
			// Alone mode
			ts3client.OnAloneChanged += OnAloneChanged;
			ts3client.OnAloneChanged += (s, e) => customTarget.Alone = e.Alone;
			// Whisper stall
			ts3client.OnWhisperNoTarget += (s, e) => player.SetStall();

			commandManager.RegisterCollection(MainCommands.Bag);
			// TODO remove after plugin rework
			var pluginManager = Injector.GetModuleOrThrow<PluginManager>();
			foreach (var plugin in pluginManager.Plugins)
				if (plugin.Type == PluginType.CorePlugin || plugin.Type == PluginType.Commands)
					commandManager.RegisterCollection(plugin.CorePlugin.Bag);
			// Restore all alias from the config
			foreach (var alias in config.Commands.Alias.GetAllItems())
				commandManager.RegisterAlias(alias.Key, alias.Value).UnwrapToLog(Log);
		}

		public async Task<E<string>> Run()
		{
			Log.Info("Bot \"{0}\" connecting to \"{1}\"", config.Name, config.Connect.Address);
			var result = await ts3client.Connect();
			if (!result.Ok)
				return result;

			EnableIdleTickWorker();

			var badges = config.Connect.Badges.Value;
			if (!string.IsNullOrEmpty(badges))
				ts3client?.ChangeBadges(badges);

			var onStart = config.Events.OnConnect.Value;
			if (!string.IsNullOrEmpty(onStart))
			{
				var info = CreateExecInfo();
				await CallScript(info, onStart, false, true);
			}

			return R.Ok;
		}

		public async Task Stop()
		{
			ValidateScheduler();

			Injector.GetModule<BotManager>()?.RemoveBot(this);

			if (!IsDisposed) IsDisposed = true;
			else return;

			Log.Info("Bot ({0}) disconnecting.", Id);

			DisableIdleTickWorker();

			Injector.GetModule<PluginManager>()?.StopPlugins(this);
			Injector.GetModule<PlayManager>()?.Stop();
			Injector.GetModule<Player>()?.Dispose();
			var tsClient = Injector.GetModule<Ts3Client>();
			if (tsClient != null)
				await tsClient.Disconnect();
			Injector.GetModule<DedicatedTaskScheduler>()?.Dispose();
			config.ClearEvents();
		}

		private async void OnBotDisconnect(object? sender, DisconnectEventArgs e)
		{
			DisableIdleTickWorker();

			var onStop = config.Events.OnDisconnect.Value;
			if (!string.IsNullOrEmpty(onStop))
			{
				var info = CreateExecInfo();
				await CallScript(info, onStop, false, true);
			}

			await Stop();
		}

		private async void OnMessageReceived(object? sender, TextMessage textMessage)
		{
			if (textMessage?.Message == null)
			{
				Log.Warn("Invalid TextMessage: {@textMessage}", textMessage);
				return;
			}
			Log.Debug("TextMessage: {@textMessage}", textMessage);

			var langResult = await LocalizationManager.LoadLanguage(config.Language, false);
			if (!langResult.Ok)
				Log.Error("Failed to load language file ({0})", langResult.Error);

			textMessage.Message = textMessage.Message.TrimStart(' ');
			if (!textMessage.Message.StartsWith("!", StringComparison.Ordinal))
				return;

			Log.Info("User {0} requested: {1}", textMessage.InvokerName, textMessage.Message);

			ts3client.InvalidateClientBuffer();

			ChannelId? channelId = null;
			ClientDbId? databaseId = null;
			ChannelGroupId? channelGroup = null;
			ServerGroupId[]? serverGroups = null;

			if (ts3FullClient.Book.Clients.TryGetValue(textMessage.InvokerId, out var bookClient))
			{
				channelId = bookClient.Channel;
				databaseId = bookClient.DatabaseId;
				serverGroups = bookClient.ServerGroups.ToArray();
				channelGroup = bookClient.ChannelGroup;
			}
			else if ((await ts3FullClient.ClientInfo(textMessage.InvokerId)).Get(out var infoClient, out var infoClientError))
			{
				channelId = infoClient.ChannelId;
				databaseId = infoClient.DatabaseId;
				serverGroups = infoClient.ServerGroups;
				channelGroup = infoClient.ChannelGroup;
			}
			else
			{
				try
				{
					var cachedClient = await ts3client.GetCachedClientById(textMessage.InvokerId);
					channelId = cachedClient.ChannelId;
					databaseId = cachedClient.DatabaseId;
					channelGroup = cachedClient.ChannelGroup;
				}
				catch (AudioBotException cachedClientError)
				{
					Log.Warn(
						"The bot is missing teamspeak permissions to view the communicating client. " +
						"Some commands or permission checks might not work " +
						"(clientlist:{0}, clientinfo:{1}).",
						cachedClientError.Message, infoClientError.ErrorFormat());
				}
			}

			var invoker = new ClientCall(textMessage.InvokerUid ?? Uid.Anonymous, textMessage.Message,
				clientId: textMessage.InvokerId,
				visibiliy: textMessage.Target,
				nickName: textMessage.InvokerName,
				channelId: channelId,
				databaseId: databaseId,
				serverGroups: serverGroups,
				channelGroup: channelGroup);

			var session = sessionManager.GetOrCreateSession(textMessage.InvokerId);
			var info = CreateExecInfo(invoker, session);

			// check if the user has an open request
			if (session.ResponseProcessor != null)
			{
				await TryCatchCommand(info, answer: true, async () =>
				{
					var msg = await session.ResponseProcessor(textMessage.Message);
					if (!string.IsNullOrEmpty(msg))
						await info.Write(msg).CatchToLog(Log);
				});
				session.ClearResponse();
				return;
			}

			await CallScript(info, textMessage.Message, answer: true, false);
		}

		private void OnClientLeftView(object? sender, ClientLeftView eventArgs)
		{
			targetManager.WhisperClientUnsubscribe(eventArgs.ClientId);
			sessionManager.RemoveSession(eventArgs.ClientId);
		}

		#region Status: Description, Avatar

		public Task UpdateBotStatus()
		{
			return Scheduler.InvokeAsync(UpdateBotStatusInternal);
		}

		public async Task UpdateBotStatusInternal()
		{
			ValidateScheduler();

			if (IsDisposed)
				return;

			if (!config.SetStatusDescription)
				return;

			string? setString;
			if (playManager.IsPlaying)
			{
				setString = QuizMode
					? strings.info_botstatus_quiztime
					: playManager.CurrentPlayData?.ResourceData?.ResourceTitle;
			}
			else
			{
				setString = strings.info_botstatus_sleeping;
			}

			await ts3client.ChangeDescription(setString ?? "").CatchToLog(Log);
		}

		public Task GenerateStatusImage(bool playing, PlayInfoEventArgs? startEvent)
		{
			return Scheduler.InvokeAsync(() => GenerateStatusImageInternal(playing, startEvent));
		}

		public async Task GenerateStatusImageInternal(bool playing, PlayInfoEventArgs? startEvent)
		{
			ValidateScheduler();

			if (!config.GenerateStatusAvatar || IsDisposed)
				return;

			static Stream? GetRandomFile(string? basePath, string prefix)
			{
				try
				{
					if (string.IsNullOrEmpty(basePath))
						return null;
					var avatarPath = new DirectoryInfo(Path.Combine(basePath, BotPaths.Avatars));
					if (!avatarPath.Exists)
						return null;
					var avatars = avatarPath.EnumerateFiles(prefix).ToArray();
					if (avatars.Length == 0)
						return null;
					var pickedAvatar = Tools.PickRandom(avatars);
					return pickedAvatar.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "Failed to load local avatar");
					return null;
				}
			}

			Stream? setStream = null;
			if (playing)
			{
				if (startEvent != null && !QuizMode)
				{
					try
					{
						using var thumbStream = await resourceResolver.GetThumbnail(startEvent.PlayResource);
						setStream = (await ImageUtil.ResizeImageSave(thumbStream)).Stream;
					}
					catch (AudioBotException ex) { Log.Debug(ex, "Failed to fetch thumbnail image"); }
				}
				setStream ??= GetRandomFile(config.LocalConfigDir, "play*");
			}
			else
			{
				setStream ??= GetRandomFile(config.LocalConfigDir, "sleep*");
				setStream ??= Util.GetEmbeddedFile("TS3AudioBot.Media.SleepingKitty.png");
			}

			if (setStream != null)
			{
				try
				{
					using (setStream)
					{
						await ts3client.UploadAvatar(setStream);
					}
				}
				catch (Exception ex)
				{
					Log.Warn(ex, "Could not change avatar");
				}
			}
			else
			{
				await ts3FullClient.DeleteAvatar().CatchToLog(Log);
			}
		}

		#endregion

		private async void BeforeResourceStarted(object? sender, PlayInfoEventArgs e)
		{
			const string DefaultVoiceScript = "!whisper off";
			const string DefaultWhisperScript = "!xecute (!whisper subscription) (!unsubscribe temporary) (!subscribe channeltemp (!getmy channel))";

			var mode = config.Audio.SendMode.Value;
			string script;
			if (mode.StartsWith("!", StringComparison.Ordinal))
				script = mode;
			else if (mode.Equals("voice", StringComparison.OrdinalIgnoreCase))
				script = DefaultVoiceScript;
			else if (mode.Equals("whisper", StringComparison.OrdinalIgnoreCase))
				script = DefaultWhisperScript;
			else
			{
				Log.Error("Invalid voice mode");
				return;
			}

			var info = CreateExecInfo(e.Invoker);
			await CallScript(info, script, false, true);
		}

		private async Task CallScript(ExecutionInformation info, string command, bool answer, bool skipRights)
		{
			Log.Debug("Calling script (skipRights:{0}, answer:{1}): {2}", skipRights, answer, command);
			stats.TrackCommandCall(answer);

			info.AddModule(new CallerInfo(false)
			{
				SkipRightsChecks = skipRights,
				CommandComplexityMax = config.Commands.CommandComplexity,
				IsColor = config.Commands.Color,
			});

			await TryCatchCommand(info, answer, async () =>
			{
				// parse and execute the command
				var res = await CommandManager.Execute(info, command);

				if (!answer)
					return;

				// Write result to user
				var s = res.AsString();
				if (!string.IsNullOrEmpty(s))
					await info.Write(s).CatchToLog(Log);
			});
		}

		private ExecutionInformation CreateExecInfo(InvokerData? invoker = null, UserSession? session = null)
		{
			var info = new ExecutionInformation(Injector);
			if (invoker is ClientCall ci)
				info.AddModule(ci);
			info.AddModule(invoker ?? InvokerData.Anonymous);
			info.AddModule(session ?? new AnonymousSession());
			info.AddModule(Filter.GetFilterByNameOrDefault(config.Commands.Matcher));
			return info;
		}

		#region Event: Idle

		private async void OnIdle()
		{
			// DisableIdleTickWorker(); // fire once only ??

			var onIdle = config.Events.OnIdle.Value;
			if (!string.IsNullOrEmpty(onIdle))
			{
				var info = CreateExecInfo();
				await CallScript(info, onIdle, false, true);
			}
		}

		private void EnableIdleTickWorker()
		{
			var idleTime = config.Events.IdleDelay.Value;
			if (idleTime <= TimeSpan.Zero || string.IsNullOrEmpty(config.Events.OnIdle.Value))
			{
				DisableIdleTickWorker();
				return;
			}
			idleTickWorker.Interval = idleTime;
			idleTickWorker.Active = true;
		}

		private void DisableIdleTickWorker() => idleTickWorker.Active = false;

		#endregion

		#region Event: Alone/Party

		private async void OnAloneChanged(object? sender, AloneChanged e)
		{
			ValidateScheduler();

			string script;
			TimeSpan delay;
			if (e.Alone)
			{
				script = config.Events.OnAlone.Value;
				delay = config.Events.AloneDelay.Value;
			}
			else
			{
				script = config.Events.OnParty.Value;
				delay = config.Events.PartyDelay.Value;
			}
			if (string.IsNullOrEmpty(script))
				return;

			if (delay > TimeSpan.Zero) // TODO: Async (Add cancellation token for better consistency)
				await Task.Delay(delay);

			var info = CreateExecInfo();
			await CallScript(info, script, false, true);
		}

		#endregion

		private async Task TryCatchCommand(ExecutionInformation info, bool answer, Func<Task> action)
		{
			try
			{
				await action.Invoke();
			}
			catch (AudioBotException ex)
			{
				NLog.LogLevel commandErrorLevel = answer ? NLog.LogLevel.Debug : NLog.LogLevel.Warn;
				Log.Log(commandErrorLevel, ex, "Command Error ({0})", ex.Message);
				if (answer)
				{
					await info.Write(TextMod.Format(config.Commands.Color, strings.error_call_error.Mod().Color(Color.Red).Bold(), ex.Message))
						.CatchToLog(Log);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Unexpected command error: {0}", ex.Message);
				if (answer)
				{
					await info.Write(TextMod.Format(config.Commands.Color, strings.error_call_unexpected_error.Mod().Color(Color.Red).Bold(), ex.Message))
						.CatchToLog(Log);
				}
			}
		}

		private void ValidateScheduler()
		{
			if (TaskScheduler.Current != Scheduler)
			{
				var stack = new System.Diagnostics.StackTrace();
				Log.Error("Current call is not scheduled correctly. Sched: {0}, Own: {1}. Stack: {2}",
					TaskScheduler.Current.Id,
					Scheduler.Id,
					stack
				);
			}
		}

		public BotInfo GetInfo() => new BotInfo
		{
			Id = Id,
			Name = config.Name,
			Server = ts3FullClient.ConnectionData?.Address,
			Status = ts3FullClient.Connected ? BotStatus.Connected : BotStatus.Connecting,
		};
	}

	public class BotInfo
	{
		public int? Id { get; set; }
		public string? Name { get; set; }
		public string? Server { get; set; }
		public BotStatus Status { get; set; }

		public override string ToString() => $"Id: {Id} Name: {Name} Server: {Server} Status: {Status}"; // LOC: TODO
	}

	public enum BotStatus
	{
		Offline,
		Connecting,
		Connected,
	}
}

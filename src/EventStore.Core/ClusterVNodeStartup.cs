using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Common.Configuration;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Cluster;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Plugins.Authentication;
using EventStore.Plugins.Authorization;
using EventStore.Plugins.Subsystems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using MidFunc = System.Func<
	Microsoft.AspNetCore.Http.HttpContext,
	System.Func<System.Threading.Tasks.Task>,
	System.Threading.Tasks.Task
>;
using Operations = EventStore.Core.Services.Transport.Grpc.Operations;
using ClusterGossip = EventStore.Core.Services.Transport.Grpc.Cluster.Gossip;
using ClientGossip = EventStore.Core.Services.Transport.Grpc.Gossip;
using ServerFeatures = EventStore.Core.Services.Transport.Grpc.ServerFeatures;

namespace EventStore.Core {
	public class ClusterVNodeStartup<TStreamId> : IStartup, IHandle<SystemMessage.SystemReady>,
		IHandle<SystemMessage.BecomeShuttingDown> {

		private readonly ISubsystem[] _subsystems;
		private readonly IPublisher _mainQueue;
		private readonly IPublisher _monitoringQueue;
		private readonly ISubscriber _mainBus;
		private readonly IAuthenticationProvider _authenticationProvider;
		private readonly IReadOnlyList<IHttpAuthenticationProvider> _httpAuthenticationProviders;
		private readonly IReadIndex<TStreamId> _readIndex;
		private readonly int _maxAppendSize;
		private readonly TimeSpan _writeTimeout;
		private readonly IExpiryStrategy _expiryStrategy;
		private readonly KestrelHttpService _httpService;
		private readonly MetricsConfiguration _metricsConfiguration;
		private readonly Trackers _trackers;
		private readonly StatusCheck _statusCheck;

		private bool _ready;
		private readonly IAuthorizationProvider _authorizationProvider;
		private readonly MultiQueuedHandler _httpMessageHandler;
		private readonly string _clusterDns;

		public ClusterVNodeStartup(ISubsystem[] subsystems,
			IPublisher mainQueue,
			IPublisher monitoringQueue,
			ISubscriber mainBus,
			MultiQueuedHandler httpMessageHandler,
			IAuthenticationProvider authenticationProvider,
			IReadOnlyList<IHttpAuthenticationProvider> httpAuthenticationProviders,
			IAuthorizationProvider authorizationProvider,
			IReadIndex<TStreamId> readIndex,
			int maxAppendSize,
			TimeSpan writeTimeout,
			IExpiryStrategy expiryStrategy,
			KestrelHttpService httpService,
			MetricsConfiguration metricsConfiguration,
			Trackers trackers,
			string clusterDns) {
			if (subsystems == null) {
				throw new ArgumentNullException(nameof(subsystems));
			}

			if (mainQueue == null) {
				throw new ArgumentNullException(nameof(mainQueue));
			}

			if (httpAuthenticationProviders == null) {
				throw new ArgumentNullException(nameof(httpAuthenticationProviders));
			}

			if(authorizationProvider == null)
				throw new ArgumentNullException(nameof(authorizationProvider));

			if (readIndex == null) {
				throw new ArgumentNullException(nameof(readIndex));
			}

			Ensure.Positive(maxAppendSize, nameof(maxAppendSize));

			if (httpService == null) {
				throw new ArgumentNullException(nameof(httpService));
			}

			if (mainBus == null) {
				throw new ArgumentNullException(nameof(mainBus));
			}

			if (monitoringQueue == null) {
				throw new ArgumentNullException(nameof(monitoringQueue));
			}
			_subsystems = subsystems;
			_mainQueue = mainQueue;
			_monitoringQueue = monitoringQueue;
			_mainBus = mainBus;
			_httpMessageHandler = httpMessageHandler;
			_authenticationProvider = authenticationProvider;
			_httpAuthenticationProviders = httpAuthenticationProviders;
			_authorizationProvider = authorizationProvider;
			_readIndex = readIndex;
			_maxAppendSize = maxAppendSize;
			_writeTimeout = writeTimeout;
			_expiryStrategy = expiryStrategy;
			_httpService = httpService;
			_metricsConfiguration = metricsConfiguration;
			_trackers = trackers;
			_clusterDns = clusterDns;

			_statusCheck = new StatusCheck(this);
		}

		public void Configure(IApplicationBuilder app) {
			var internalDispatcher = new InternalDispatcherEndpoint(_mainQueue, _httpMessageHandler);
			_mainBus.Subscribe(internalDispatcher);

			app.Map("/health", _statusCheck.Configure)
				.UseMiddleware<AuthenticationMiddleware>()
				.UseRouting()
				.UseEndpoints(ep => {
					_authenticationProvider.ConfigureEndpoints(ep);

					ep.MapGrpcService<PersistentSubscriptions>();
					ep.MapGrpcService<Users>();
					ep.MapGrpcService<Streams<TStreamId>>();
					ep.MapGrpcService<ClusterGossip>();
					ep.MapGrpcService<Elections>();
					ep.MapGrpcService<Operations>();
					ep.MapGrpcService<ClientGossip>();
					ep.MapGrpcService<Monitoring>();
					ep.MapGrpcService<ServerFeatures>();

					// enable redaction service on unix sockets only
					ep.MapGrpcService<Redaction>().AddEndpointFilter(async (c, next) => {
						if (!c.HttpContext.IsUnixSocketConnection())
							return Results.BadRequest("Redaction is only available via Unix Sockets");
						return await next(c).ConfigureAwait(false);
					});

					// Map the legacy controller endpoints with special middleware pipeline
					ep.MapLegacyHttp(
						ep.CreateApplicationBuilder()
							// Select an appropriate controller action and codec.
							//    Success -> Add InternalContext (HttpEntityManager, urimatch, ...) to HttpContext
							//    Fail -> Pipeline terminated with response.
							.UseMiddleware<KestrelToInternalBridgeMiddleware>()

							// Looks up the InternalContext to perform the check.
							// Terminal if auth check is not successful.
							.UseMiddleware<AuthorizationMiddleware>()

							// Open telemetry currently guarded by our custom authz for consistency with stats
							.UseOpenTelemetryPrometheusScrapingEndpoint()

							// Internal dispatcher looks up the InternalContext to call the appropriate controller
							.Use(x => internalDispatcher.InvokeAsync)
							.Build(),
						_httpService);
				});

			foreach (var subsystem in _subsystems)
				subsystem.Configure(app);
		}

		IServiceProvider IStartup.ConfigureServices(IServiceCollection services) => ConfigureServices(services)
			.BuildServiceProvider();

		public IServiceCollection ConfigureServices(IServiceCollection services) =>
			_subsystems
				.Aggregate(services
						.AddRouting()
						.AddSingleton(_httpAuthenticationProviders)
						.AddSingleton(_authenticationProvider)
						.AddSingleton(_authorizationProvider)
						.AddSingleton<AuthenticationMiddleware>()
						.AddSingleton<AuthorizationMiddleware>()
						.AddSingleton(new KestrelToInternalBridgeMiddleware(_httpService.UriRouter, _httpService.LogHttpRequests, _httpService.AdvertiseAsHost, _httpService.AdvertiseAsPort))
						.AddSingleton(_readIndex)
						.AddSingleton(new Streams<TStreamId>(_mainQueue, _maxAppendSize,
							_writeTimeout, _expiryStrategy,
							_trackers.GrpcTrackers,
							_authorizationProvider))
						.AddSingleton(new PersistentSubscriptions(_mainQueue, _authorizationProvider))
						.AddSingleton(new Users(_mainQueue, _authorizationProvider))
						.AddSingleton(new Operations(_mainQueue, _authorizationProvider))
						.AddSingleton(new ClusterGossip(_mainQueue, _authorizationProvider, _clusterDns,
							updateTracker: _trackers.GossipTrackers.ProcessingPushFromPeer,
							readTracker: _trackers.GossipTrackers.ProcessingRequestFromPeer))
						.AddSingleton(new Elections(_mainQueue, _authorizationProvider, _clusterDns))
						.AddSingleton(new ClientGossip(_mainQueue, _authorizationProvider, _trackers.GossipTrackers.ProcessingRequestFromGrpcClient))
						.AddSingleton(new Monitoring(_monitoringQueue))
						.AddSingleton(new Redaction(_mainQueue, _authorizationProvider))
						.AddSingleton<ServerFeatures>()

						// OpenTelemetry
						.AddOpenTelemetry()
						.WithMetrics(meterOptions => meterOptions
							.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("eventstore"))
							.AddMeter(_metricsConfiguration.Meters)
							.AddView(i => {
								if (i.Name.StartsWith("eventstore-") &&
									i.Name.EndsWith("-latency") &&
									i.Unit == "seconds")
									return new ExplicitBucketHistogramConfiguration {
										Boundaries = new double[] {
											0.001, //    1 ms
											0.005, //    5 ms
											0.01,  //   10 ms
											0.05,  //   50 ms
											0.1,   //  100 ms
											0.5,   //  500 ms
											1,     // 1000 ms
											5,     // 5000 ms
										}
									};
								else if (i.Name.StartsWith("eventstore-") && i.Unit == "seconds")
									return new ExplicitBucketHistogramConfiguration {
										Boundaries = new double[] {
											0.000_001, // 1 microsecond
											0.000_01,
											0.000_1,
											0.001, // 1 millisecond
											0.01,
											0.1,
											1, // 1 second
											10,
										}
									};
								return default;
							})
							.AddPrometheusExporter())
						.Services

						// gRPC
						.AddSingleton<RetryInterceptor>()
						.AddGrpc(options => {
							options.Interceptors.Add<RetryInterceptor>();
						})
						.AddServiceOptions<Streams<TStreamId>>(options =>
							options.MaxReceiveMessageSize = TFConsts.EffectiveMaxLogRecordSize)
						.Services,
					(s, subsystem) => subsystem.ConfigureServices(s));

		public void Handle(SystemMessage.SystemReady _) => _ready = true;

		public void Handle(SystemMessage.BecomeShuttingDown _) => _ready = false;

		private class StatusCheck {
			private readonly ClusterVNodeStartup<TStreamId> _startup;

			public StatusCheck(ClusterVNodeStartup<TStreamId> startup) {
				if (startup == null) {
					throw new ArgumentNullException(nameof(startup));
				}

				_startup = startup;
			}

			public void Configure(IApplicationBuilder builder) =>
				builder.Use(GetAndHeadOnly)
					.UseRouter(router => router
						.MapMiddlewareGet("live", inner => inner.Use(Live)));

			private MidFunc Live => (context, next) => {
				context.Response.StatusCode = _startup._ready ? 204 : 503;
				return Task.CompletedTask;
			};

			private static MidFunc GetAndHeadOnly => (context, next) => {
				switch (context.Request.Method) {
					case "HEAD":
						context.Request.Method = "GET";
						return next();
					case "GET":
						return next();
					default:
						context.Response.StatusCode = 405;
						return Task.CompletedTask;
				}
			};
		}
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Counters;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.OAuth;
using Raven.Abstractions.Util;
using Raven.Client.Connection;
using Raven.Client.Connection.Implementation;
using Raven.Client.Connection.Profiling;
using Raven.Client.Counters.Actions;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;

namespace Raven.Client.Counters
{
	/// <summary>
	/// implements administration level counters functionality
	/// </summary>
	public class CounterStore : ICounterStore
	{
		private bool _isInitialized;
		public CounterStore()
		{
			JsonSerializer = JsonExtensions.CreateDefaultJsonSerializer();
			JsonRequestFactory = new HttpJsonRequestFactory(Constants.NumberOfCachedRequests);
			Convention = new Convention();
			Credentials = new OperationCredentials(null, CredentialCache.DefaultNetworkCredentials);
			Advanced = new CounterStoreAdvancedOperations(this);
			_batch = new Lazy<BatchOperationsStore>(() => new BatchOperationsStore(this));
			_isInitialized = false;
		}

		public void Initialize(bool ensureDefaultCounterExists = false)
		{
			if(_isInitialized)
				throw new InvalidOperationException("CounterStore already initialized.");
			_isInitialized = true;
			InitializeSecurity();

			if (ensureDefaultCounterExists && !string.IsNullOrWhiteSpace(DefaultCounterStorageName))
			{
				if (String.IsNullOrWhiteSpace(DefaultCounterStorageName))
					throw new InvalidOperationException("DefaultCounterStorageName is null or empty and ensureDefaultCounterExists = true --> cannot create default counter storage with empty name");

				CreateCounterStorageAsync(new CounterStorageDocument
				{
					Settings = new Dictionary<string, string>
					{
						{"Raven/Counters/DataDir", @"~\Counters\" + DefaultCounterStorageName}
					},
				}, DefaultCounterStorageName).Wait();
			}
		}

		private readonly Lazy<BatchOperationsStore> _batch;

		public BatchOperationsStore Batch
		{
			get { return _batch.Value; }
		}

		public OperationCredentials Credentials { get; set; }

		public HttpJsonRequestFactory JsonRequestFactory { get; set; }

		public string Url { get; set; }

		public string DefaultCounterStorageName { get; set; }

		public Convention Convention { get; set; }

		public JsonSerializer JsonSerializer { get; set; }

		public CounterStoreAdvancedOperations Advanced { get; private set; }

		/// <summary>
		/// Create new counter storage on the server.
		/// </summary>
		/// <param name="counterStorageDocument">Settings for the counter storage. If null, default settings will be used, and the name specified in the client ctor will be used</param>
		/// <param name="counterStorageName">Override counter storage name specified in client ctor. If null, the name already specified will be used</param>
		public async Task CreateCounterStorageAsync(CounterStorageDocument counterStorageDocument, string counterStorageName, bool shouldUpateIfExists = false, CancellationToken token = default(CancellationToken))
		{
			if (counterStorageDocument == null)
				throw new ArgumentNullException("counterStorageDocument");

			var urlTemplate = "{0}/admin/cs/{1}";
			if (shouldUpateIfExists)
				urlTemplate += "?update=true";

			var requestUriString = String.Format(urlTemplate, Url, counterStorageName);

			using (var request = CreateHttpJsonRequest(requestUriString, HttpMethods.Put))
			{
				try
				{
					await request.WriteAsync(RavenJObject.FromObject(counterStorageDocument)).WithCancellation(token).ConfigureAwait(false);
				}
				catch (ErrorResponseException e)
				{
					if (e.StatusCode == HttpStatusCode.Conflict)
						throw new InvalidOperationException("Cannot create counter storage with the name '" + counterStorageName + "' because it already exists. Use CreateOrUpdateCounterStorageAsync in case you want to update an existing counter storage", e);

					throw;
				}					
			}
		}

		public async Task DeleteCounterStorageAsync(string counterStorageName, bool hardDelete = false, CancellationToken token = default(CancellationToken))
		{
			var requestUriString = String.Format("{0}/admin/cs/{1}?hard-delete={2}", Url, counterStorageName, hardDelete);

			using (var request = CreateHttpJsonRequest(requestUriString, HttpMethods.Delete))
			{
				try
				{
					await request.ExecuteRequestAsync().WithCancellation(token).ConfigureAwait(false);
				}
				catch (ErrorResponseException e)
				{
					if (e.StatusCode == HttpStatusCode.NotFound)
						throw new InvalidOperationException(string.Format("Counter storage with specified name ({0}) doesn't exist", counterStorageName));
					throw;
				}
			}
		}

		public CountersClient NewCounterClient(string counterStorageName = null)
		{
			if (counterStorageName == null && String.IsNullOrWhiteSpace(DefaultCounterStorageName))
				throw new ArgumentNullException("counterStorageName", 
					@"counterStorageName is null and default counter storage name is empty - 
						this means no default counter exists.");
			return new CountersClient(this,counterStorageName ?? DefaultCounterStorageName);
		}

		public async Task<string[]> GetCounterStoragesNamesAsync(CancellationToken token = default(CancellationToken))
		{
			var requestUriString = String.Format("{0}/cs/counterStorageNames", Url);

			using (var request = CreateHttpJsonRequest(requestUriString, HttpMethods.Get))
			{
				var response = await request.ReadResponseJsonAsync().WithCancellation(token).ConfigureAwait(false);
				return response.ToObject<string[]>(JsonSerializer);
			}
		}

		protected HttpJsonRequest CreateHttpJsonRequest(string requestUriString, HttpMethod httpMethod, bool disableRequestCompression = false, bool disableAuthentication = false)
		{
			return JsonRequestFactory.CreateHttpJsonRequest(new CreateHttpJsonRequestParams(this, requestUriString, httpMethod, Credentials, Convention)
			{
				DisableRequestCompression = disableRequestCompression,
				DisableAuthentication = disableAuthentication
			});
		}

		public ProfilingInformation ProfilingInformation { get; private set; }


		private void InitializeSecurity()
		{
			if (Convention.HandleUnauthorizedResponseAsync != null)
				return; // already setup by the user

			if (string.IsNullOrEmpty(Credentials.ApiKey) == false)
				Credentials = null;

			var basicAuthenticator = new BasicAuthenticator(JsonRequestFactory.EnableBasicAuthenticationOverUnsecuredHttpEvenThoughPasswordsWouldBeSentOverTheWireInClearTextToBeStolenByHackers);
			var securedAuthenticator = new SecuredAuthenticator();

			JsonRequestFactory.ConfigureRequest += basicAuthenticator.ConfigureRequest;
			JsonRequestFactory.ConfigureRequest += securedAuthenticator.ConfigureRequest;

			Convention.HandleForbiddenResponseAsync = (forbiddenResponse, credentials) =>
			{
				if (credentials.ApiKey == null)
				{
					AssertForbiddenCredentialSupportWindowsAuth(forbiddenResponse);
					return null;
				}

				return null;
			};

			Convention.HandleUnauthorizedResponseAsync = (unauthorizedResponse, credentials) =>
			{
				var oauthSource = unauthorizedResponse.Headers.GetFirstValue("OAuth-Source");

#if DEBUG && FIDDLER
                // Make sure to avoid a cross DNS security issue, when running with Fiddler
				if (string.IsNullOrEmpty(oauthSource) == false)
					oauthSource = oauthSource.Replace("localhost:", "localhost.fiddler:");
#endif

				// Legacy support
				if (string.IsNullOrEmpty(oauthSource) == false &&
					oauthSource.EndsWith("/OAuth/API-Key", StringComparison.CurrentCultureIgnoreCase) == false)
				{
					return basicAuthenticator.HandleOAuthResponseAsync(oauthSource, credentials.ApiKey);
				}

				if (credentials.ApiKey == null)
				{
					AssertUnauthorizedCredentialSupportWindowsAuth(unauthorizedResponse, credentials.Credentials);
					return null;
				}

				if (string.IsNullOrEmpty(oauthSource))
					oauthSource = Url + "/OAuth/API-Key";

				return securedAuthenticator.DoOAuthRequestAsync(Url, oauthSource, credentials.ApiKey);
			};

		}

		private void AssertForbiddenCredentialSupportWindowsAuth(HttpResponseMessage response)
		{
			if (Credentials == null)
				return;

			var requiredAuth = response.Headers.GetFirstValue("Raven-Required-Auth");
			if (requiredAuth == "Windows")
			{
				// we are trying to do windows auth, but we didn't get the windows auth headers
				throw new SecurityException(
					"Attempted to connect to a RavenDB Server that requires authentication using Windows credentials, but the specified server does not support Windows authentication." +
					Environment.NewLine +
					"If you are running inside IIS, make sure to enable Windows authentication.");
			}
		}

		private void AssertUnauthorizedCredentialSupportWindowsAuth(HttpResponseMessage response, ICredentials credentials)
		{
			if (credentials == null)
				return;

			var authHeaders = response.Headers.WwwAuthenticate.FirstOrDefault();
			if (authHeaders == null ||
				(authHeaders.ToString().Contains("NTLM") == false && authHeaders.ToString().Contains("Negotiate") == false)
				)
			{
				// we are trying to do windows auth, but we didn't get the windows auth headers
				throw new SecurityException(
					"Attempted to connect to a RavenDB Server that requires authentication using Windows credentials," + Environment.NewLine
					+ " but either wrong credentials where entered or the specified server does not support Windows authentication." +
					Environment.NewLine +
					"If you are running inside IIS, make sure to enable Windows authentication.");
			}
		}

		public void Dispose()
		{
			if(_batch.IsValueCreated)
				_batch.Value.Dispose();

			
		}

		public class BatchOperationsStore : ICountersBatchOperation
		{
			private readonly ICounterStore _parent;
			private readonly Lazy<CountersBatchOperation> _defaultBatchOperation;
			private readonly ConcurrentDictionary<string, CountersBatchOperation> _batchOperations;

			public BatchOperationsStore(ICounterStore parent)
			{				
				_batchOperations = new ConcurrentDictionary<string, CountersBatchOperation>();
				_parent = parent;
				if(String.IsNullOrWhiteSpace(parent.DefaultCounterStorageName) == false)
					_defaultBatchOperation = new Lazy<CountersBatchOperation>(() => new CountersBatchOperation(parent, parent.DefaultCounterStorageName));
			}

			public ICountersBatchOperation this[string storageName]
			{
				get { return GetOrCreateBatchOperation(storageName); }
			}

			private ICountersBatchOperation GetOrCreateBatchOperation(string storageName)
			{
				return _batchOperations.GetOrAdd(storageName, arg => new CountersBatchOperation(_parent, storageName));
			}

			public void Dispose()
			{
				_batchOperations.Values
					.ForEach(operation => operation.Dispose());
				if (_defaultBatchOperation != null && _defaultBatchOperation.IsValueCreated)
					_defaultBatchOperation.Value.Dispose();
			}

			public void ScheduleChange(string groupName, string counterName, long delta)
			{
				if (string.IsNullOrWhiteSpace(_parent.DefaultCounterStorageName))
					throw new InvalidOperationException("Default counter storage name cannot be empty!");

				_defaultBatchOperation.Value.ScheduleChange(groupName, counterName, delta);
			}

			public void ScheduleIncrement(string groupName, string counterName)
			{
				if (string.IsNullOrWhiteSpace(_parent.DefaultCounterStorageName))
					throw new InvalidOperationException("Default counter storage name cannot be empty!");

				_defaultBatchOperation.Value.ScheduleIncrement(groupName, counterName);
			}

			public void ScheduleDecrement(string groupName, string counterName)
			{
				if (string.IsNullOrWhiteSpace(_parent.DefaultCounterStorageName))
					throw new InvalidOperationException("Default counter storage name cannot be empty!");

				_defaultBatchOperation.Value.ScheduleDecrement(groupName, counterName);
			}

			public async Task FlushAsync()
			{
				if (string.IsNullOrWhiteSpace(_parent.DefaultCounterStorageName))
					throw new InvalidOperationException("Default counter storage name cannot be empty!");

				await _defaultBatchOperation.Value.FlushAsync();
			}


			public CountersBatchOptions Options
			{
				get
				{
					if (string.IsNullOrWhiteSpace(_parent.DefaultCounterStorageName))
						throw new InvalidOperationException("Default counter storage name cannot be empty!");
					return _defaultBatchOperation.Value.Options;
				}
			}
		}

		public class CounterStoreAdvancedOperations
		{
			private readonly ICounterStore parent;

			internal CounterStoreAdvancedOperations(ICounterStore parent)
			{
				this.parent = parent;
			}

			public ICountersBatchOperation NewBatch(string counterStorageName, CountersBatchOptions options = null)
			{
				return new CountersBatchOperation(parent, counterStorageName, options);
			}
		}
	}
}
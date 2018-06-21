﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bitfinex.Net.Converters;
using Bitfinex.Net.Objects;
using Bitfinex.Net.Objects.SocketObjects;
using CryptoExchange.Net;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Implementation;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bitfinex.Net
{
    public class BitfinexSocketClient: ExchangeClient
    {
        #region fields
        private static BitfinexSocketClientOptions defaultOptions = new BitfinexSocketClientOptions();

        private TimeSpan socketReceiveTimeout;
        private TimeSpan subscribeResponseTimeout;
        private TimeSpan orderActionConfirmationTimeout;

        private string baseAddress;
        private IWebsocket socket;

        private AutoResetEvent messageEvent;
        private AutoResetEvent sendEvent;
        private ConcurrentQueue<string> receivedMessages;
        private ConcurrentQueue<string> toSendMessages;

        private Dictionary<string, Type> subscriptionResponseTypes;

        private ConcurrentDictionary<BitfinexNewOrder, WaitAction<BitfinexOrder>> pendingOrders;
        private ConcurrentDictionary<long, WaitAction<bool>> pendingCancels;
        private ConcurrentDictionary<long, WaitAction<bool>> pendingUpdates;

        private List<SubscriptionRequest> subscriptionRequests;
        private List<SubscriptionRequest> outstandingSubscriptionRequests;
        private List<SubscriptionRequest> confirmedRequests;
        private List<UnsubscriptionRequest> outstandingUnsubscriptionRequests;
        private List<SubscriptionRegistration> registrations;

        private object confirmedRequestLock = new object();
        private object subscriptionRequestsLock = new object();
        private object outstandingSubscriptionRequestsLock = new object();
        private object outstandingUnsubscriptionRequestsLock = new object();
        private object registrationsLock = new object();

        private Task sendTask;

        private bool running;
        private bool reconnect = true;
        private bool lost;

        private SocketState state = SocketState.Disconnected;
        public SocketState State
        {
            get => state;
            private set
            {
                log.Write(LogVerbosity.Debug, $"Socket state change {state} => {value}");
                state = value;
            }
        }
        
        private readonly object connectionLock = new object();
        private static readonly object streamIdLock = new object();
        private static readonly object nonceLock = new object();
        private static int lastStreamId;
        private static long lastNonce;
        private bool authenticating;
        private bool authenticated;

        internal static string Nonce
        {
            get
            {
                lock (nonceLock)
                {
                    if (lastNonce == 0)
                        lastNonce = (long)Math.Round((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds * 1000);

                    lastNonce += 1;
                    return lastNonce.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        private static int NextStreamId
        {
            get
            {
                lock (streamIdLock)
                {
                    lastStreamId -= 1;
                    return lastStreamId;
                }
            }
        }
        #endregion

        #region events
        /// <summary>
        /// Happens when the server signals the client to pause activity
        /// </summary>
        public event Action SocketPaused;
        /// <summary>
        /// Hapens when the server signals the client it can resume activity
        /// </summary>
        public event Action SocketResumed;
        /// <summary>
        /// Happens when the socket loses connection to the server
        /// </summary>
        public event Action ConnectionLost;
        /// <summary>
        /// Happens when connection to the server is restored after it was lost
        /// </summary>
        public event Action ConnectionRestored;
        #endregion

        #region properties
        public IWebsocketFactory SocketFactory { get; set; } = new WebsocketFactory();
        #endregion

        #region ctor
        /// <summary>
        /// Create a new instance of BinanceClient using the default options
        /// </summary>
        public BitfinexSocketClient(): this(defaultOptions)
        {
        }

        /// <summary>
        /// Create a new instance of BinanceClient using provided options
        /// </summary>
        /// <param name="options">The options to use for this client</param>
        public BitfinexSocketClient(BitfinexSocketClientOptions options): base(options, options.ApiCredentials == null ? null : new BitfinexAuthenticationProvider(options.ApiCredentials))
        {
            Init();
            Configure(options);
        }
        #endregion

        #region methods
        /// <summary>
        /// Sets the default options to use for new clients
        /// </summary>
        /// <param name="options">The options to use for new clients</param>
        public static void SetDefaultOptions(BitfinexSocketClientOptions options)
        {
            defaultOptions = options;
        }

        /// <summary>
        /// Set the API key and secret
        /// </summary>
        /// <param name="apiKey">The api key</param>
        /// <param name="apiSecret">The api secret</param>
        public void SetApiCredentials(string apiKey, string apiSecret)
        {
            SetAuthenticationProvider(new BitfinexAuthenticationProvider(new ApiCredentials(apiKey, apiSecret)));
            Authenticate();
        }
        
        /// <summary>
        /// Connect to the websocket and start processing data
        /// </summary>
        /// <returns></returns>
        public bool Start()
        {
            lost = false;
            reconnect = true;
            return StartInternal();
        }

        private bool StartInternal()
        {
            lock (connectionLock)
            {
                if (CheckConnection())
                    return true;

                reconnect = true;
                State = SocketState.Connecting;
                if (socket == null)
                    Create();

                var result = Open().Result;
                State = result ? SocketState.Connected : SocketState.Disconnected;
                return result;
            }
        }

        /// <summary>
        /// Disconnect from the socket and clear all subscriptions
        /// </summary>
        public void Stop()
        {
            lost = false;
            reconnect = false;
            StopInternal();
        }

        private void StopInternal()
        {
            lock (connectionLock)
            {
                if (socket != null && socket.IsOpen)
                {
                    State = SocketState.Disconnecting;
                    socket.Close().Wait();
                    State = SocketState.Disconnected;
                }
            }

            running = false;
            messageEvent.Set();
            sendEvent.Set();

            sendTask.Wait();

            Init();
        }

        /// <summary>
        /// Synchronized version of the <see cref="PlaceOrderAsync"/> method
        /// </summary>
        /// <returns></returns>
        public CallResult<BitfinexOrder> PlaceOrder(OrderType type, string symbol, decimal amount, long? groupId = null, long? clientOrderId = null, decimal? price = null, decimal? priceTrailing = null, decimal? priceAuxiliaryLimit = null, decimal? priceOcoStop = null, OrderFlags? flags = null) => PlaceOrderAsync(type, symbol, amount, groupId, clientOrderId, price, priceTrailing, priceAuxiliaryLimit, priceOcoStop, flags).Result;

        /// <summary>
        /// Places a new order
        /// </summary>
        /// <param name="type">The type of the order</param>
        /// <param name="symbol">The symbol the order is for</param>
        /// <param name="amount">The amount of the order, positive for buying, negative for selling</param>
        /// <param name="groupId">Group id to assign to the order</param>
        /// <param name="clientOrderId">Client order id to assign to the order</param>
        /// <param name="price">Price of the order</param>
        /// <param name="priceTrailing">Trailing price of the order</param>
        /// <param name="priceAuxiliaryLimit">Auxiliary limit price of the order</param>
        /// <param name="priceOcoStop">Oco stop price of ther order</param>
        /// <param name="flags">Additional flags</param>
        /// <returns></returns>
        public async Task<CallResult<BitfinexOrder>> PlaceOrderAsync(OrderType type, string symbol, decimal amount, long? groupId = null, long? clientOrderId = null, decimal? price = null, decimal? priceTrailing = null, decimal? priceAuxiliaryLimit = null, decimal? priceOcoStop = null, OrderFlags? flags = null)
        {
            if (!CheckConnection())
                return new CallResult<BitfinexOrder>(null, new WebError("Socket needs to be started before placing an order, call the Start() method prior prior to this"));

            if (State == SocketState.Paused)
                return new CallResult<BitfinexOrder>(null, new WebError("Socket is currently paused on request of the server, pause should take max 120 seconds"));

            if (!CheckAuthentication())
                return new CallResult<BitfinexOrder>(null, new NoApiCredentialsError());
            
            log.Write(LogVerbosity.Info, "Going to place order");
            var order = new BitfinexNewOrder()
            {
                Amount = amount,
                OrderType = type,
                Symbol = symbol,
                Price = price,
                ClientOrderId = clientOrderId,
                Flags = flags,
                GroupId = groupId,
                PriceAuxiliaryLimit = priceAuxiliaryLimit,
                PriceOCOStop = priceOcoStop,
                PriceTrailing = priceTrailing
            };

            var wrapper = new object[] { 0, "on", null, order };
            var data = JsonConvert.SerializeObject(wrapper, new JsonSerializerSettings()
            {
                NullValueHandling = NullValueHandling.Ignore,
                Culture = CultureInfo.InvariantCulture
            });

            CallResult<BitfinexOrder> orderConfirm = null;
            await Task.Run(() =>
            {
                var waitAction = new WaitAction<BitfinexOrder>();
                pendingOrders.TryAdd(order, waitAction);
                Send(data);
                orderConfirm = waitAction.Wait((int)Math.Round(orderActionConfirmationTimeout.TotalMilliseconds, 0));
                pendingOrders.TryRemove(order, out waitAction);
            }).ConfigureAwait(false);

            if (orderConfirm != null && orderConfirm.Success)
                log.Write(LogVerbosity.Info, "Order placed");

            return orderConfirm ?? new CallResult<BitfinexOrder>(null,  new ServerError("No confirmation received for placed order"));
        }

        /// <summary>
        /// Synchronized version of the <see cref="CancelOrderAsync"/> method
        /// </summary>
        /// <returns></returns>
        public CallResult<bool> CancelOrder(long orderId) => CancelOrderAsync(orderId).Result;

        /// <summary>
        /// Cancels an order
        /// </summary>
        /// <param name="orderId">The id of the order to cancel</param>
        /// <returns></returns>
        public async Task<CallResult<bool>> CancelOrderAsync(long orderId)
        {
            if (!CheckConnection())
                return new CallResult<bool>(false, new WebError("Socket needs to be started before canceling an order, call the Start() method prior prior to this"));

            if (State == SocketState.Paused)
                return new CallResult<bool>(false, new WebError("Socket is currently paused on request of the server, pause should take max 120 seconds"));

            if (!CheckAuthentication())
                return new CallResult<bool>(false, new NoApiCredentialsError());

            log.Write(LogVerbosity.Info, "Going to cancel order " + orderId);
            var obj = new JObject {["id"] = orderId};
            var wrapper = new JArray(0, "oc", null, obj);
            var data = JsonConvert.SerializeObject(wrapper);

            CallResult<bool> cancelConfirm = null;
            await Task.Run(() =>
            {
                var waitAction = new WaitAction<bool>();
                pendingCancels.TryAdd(orderId, waitAction);
                Send(data);
                cancelConfirm = waitAction.Wait((int)Math.Round(orderActionConfirmationTimeout.TotalMilliseconds, 0));
                pendingCancels.TryRemove(orderId, out waitAction);
            }).ConfigureAwait(false);

            if (cancelConfirm != null && cancelConfirm.Success)
                log.Write(LogVerbosity.Info, "Order canceled");
            
            return cancelConfirm ?? new CallResult<bool>(false, new ServerError("No confirmation received for cancel order"));
        }

        public CallResult<bool> UpdateOrder(long orderId, decimal? price = null, decimal? amount = null, decimal? delta = null, decimal? priceAuxiliaryLimit = null, decimal? priceTrailing = null, OrderFlags? flags = null) => UpdateOrderAsync(orderId, price, amount, delta, priceAuxiliaryLimit, priceTrailing, flags).Result;

        public async Task<CallResult<bool>> UpdateOrderAsync(long orderId, decimal? price = null, decimal? amount = null, decimal? delta = null, decimal? priceAuxiliaryLimit = null, decimal? priceTrailing = null, OrderFlags? flags = null)
        {
            if (!CheckConnection())
                return new CallResult<bool>(false, new WebError("Socket needs to be started before canceling an order, call the Start() method prior prior to this"));

            if (State == SocketState.Paused)
                return new CallResult<bool>(false, new WebError("Socket is currently paused on request of the server, pause should take max 120 seconds"));

            if (!CheckAuthentication())
                return new CallResult<bool>(false, new NoApiCredentialsError());

            log.Write(LogVerbosity.Info, "Going to update order " + orderId);
            var order = new BitfinexUpdateOrder()
            {
                OrderId = orderId,
                Amount = amount,
                Price = price,
                Flags = flags,
                PriceAuxiliaryLimit = priceAuxiliaryLimit,
                PriceTrailing = priceTrailing
            };

            var wrapper = new object[] { 0, "ou", null, order };
            var data = JsonConvert.SerializeObject(wrapper, new JsonSerializerSettings()
            {
                NullValueHandling = NullValueHandling.Ignore,
                Culture = CultureInfo.InvariantCulture
            });

            CallResult<bool> updateConfirm = null;
            await Task.Run(() =>
            {
                var waitAction = new WaitAction<bool>();
                pendingUpdates.TryAdd(orderId, waitAction);
                Send(data);
                updateConfirm = waitAction.Wait((int)Math.Round(orderActionConfirmationTimeout.TotalMilliseconds, 0));
                pendingUpdates.TryRemove(orderId, out waitAction);
            }).ConfigureAwait(false);

            if (updateConfirm != null && updateConfirm.Success)
                log.Write(LogVerbosity.Info, "Order updated");

            return updateConfirm ?? new CallResult<bool>(false, new ServerError("No confirmation received for order update"));
        }

        #region subscribing
        /// <summary>
        /// Subscribes to wallet updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToWalletUpdates(Action<BitfinexWallet[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to wallet updates");
            var id = NextStreamId;

            lock(registrationsLock)
                registrations.Add(new WalletUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to order updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToOrderUpdates(Action<BitfinexOrder[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to order updates");
            var id = NextStreamId;

            lock(registrationsLock)
                registrations.Add(new OrderUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to position updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToPositionUpdates(Action<BitfinexPosition[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to position updates");
            var id = NextStreamId;

            lock (registrationsLock)
                registrations.Add(new PositionUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to trade updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToTradeUpdates(Action<BitfinexTradeDetails[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to trade updates");
            var id = NextStreamId;

            lock (registrationsLock)
                registrations.Add(new TradesUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to funding offer updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToFundingOfferUpdates(Action<BitfinexFundingOffer[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to funding offer updates");
            var id = NextStreamId;

            lock (registrationsLock)
                registrations.Add(new FundingOffersUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to funding credits updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToFundingCreditsUpdates(Action<BitfinexFundingCredit[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to funding credit updates");
            var id = NextStreamId;

            lock (registrationsLock)
                registrations.Add(new FundingCreditsUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to funding loans updates. A snapshot will be send when opening the socket so consider subscribing to this before opening the socket.
        /// </summary>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public CallResult<int> SubscribeToFundingLoansUpdates(Action<BitfinexFundingLoan[]> handler)
        {
            if (authProvider == null)
                return new CallResult<int>(0, new NoApiCredentialsError());

            log.Write(LogVerbosity.Debug, "Subscribing to funding loan updates");
            var id = NextStreamId;

            lock (registrationsLock)
                registrations.Add(new FundingLoansUpdateRegistration(handler, id));

            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to ticker updates for a symbol. Requires socket to be connected
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public async Task<CallResult<int>> SubscribeToTickerUpdates(string symbol, Action<BitfinexMarketOverview[]> handler)
        {
            var id = NextStreamId;
            var sub = new TickerSubscriptionRequest(symbol, handler) {StreamId = id};

            lock(subscriptionRequestsLock)
                subscriptionRequests.Add(sub);

            if (State != SocketState.Connected)
                return new CallResult<int>(id, null);

            log.Write(LogVerbosity.Debug, "Subscribing to ticker updates for " + symbol);
            await SubscribeAndWait(sub).ConfigureAwait(false);
            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to trade updates for a symbol. Requires socket to be connected
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public async Task<CallResult<int>> SubscribeToTradeUpdates(string symbol, Action<BitfinexTradeSimple[]> handler)
        {
            var id = NextStreamId;
            var sub = new TradesSubscriptionRequest(symbol, handler) { StreamId = id };

            lock (subscriptionRequestsLock)
                subscriptionRequests.Add(sub);

            if (State != SocketState.Connected)
                return new CallResult<int>(id, null);

            log.Write(LogVerbosity.Debug, "Subscribing to trade updates for " + symbol);
            await SubscribeAndWait(sub).ConfigureAwait(false);
            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to orderbook update for a symbol. Requires socket to be connected
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="precision">The pricision of the udpates</param>
        /// <param name="frequency">The frequency of updates</param>
        /// <param name="length">The amount of data to receive in the initial snapshot</param>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public async Task<CallResult<int>> SubscribeToBookUpdates(string symbol, Precision precision, Frequency frequency, int length, Action<BitfinexOrderBookEntry[]> handler)
        {
            var id = NextStreamId;
            var sub = new BookSubscriptionRequest(symbol, JsonConvert.SerializeObject(precision, new PrecisionConverter(false)), JsonConvert.SerializeObject(frequency, new FrequencyConverter(false)), length, (data) => handler((BitfinexOrderBookEntry[])data)) { StreamId = id };

            lock (subscriptionRequestsLock)
                subscriptionRequests.Add(sub);

            if (State != SocketState.Connected)
                return new CallResult<int>(id, null);

            log.Write(LogVerbosity.Debug, "Subscribing to book updates for " + symbol);
            await SubscribeAndWait(sub).ConfigureAwait(false);
            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to raw orderbook update for a symbol. Requires socket to be connected
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="length">The amount of data to recive in the initial snapshot</param>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public async Task<CallResult<int>> SubscribeToRawBookUpdates(string symbol, int length, Action<BitfinexRawOrderBookEntry[]> handler)
        {
            var id = NextStreamId;
            var sub = new RawBookSubscriptionRequest(symbol, "R0", length, (data) => handler((BitfinexRawOrderBookEntry[])data)) { StreamId = id };

            lock (subscriptionRequestsLock)
                subscriptionRequests.Add(sub);

            if (State != SocketState.Connected)
                return new CallResult<int>(id, null);

            log.Write(LogVerbosity.Debug, "Subscribing to raw book updates for " + symbol);
            await SubscribeAndWait(sub).ConfigureAwait(false);
            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Subscribes to candle updates for a symbol. Requires socket to be connected
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="interval">The interval of the candles</param>
        /// <param name="handler">The handler for the data</param>
        /// <returns>A stream id with which can be unsubscribed</returns>
        public async Task<CallResult<int>> SubscribeToCandleUpdates(string symbol, TimeFrame interval, Action<BitfinexCandle[]> handler)
        {
            if (symbol.Length == 6)
            {
                symbol = symbol.ToUpper();
                symbol = "t" + symbol;
            }

            var id = NextStreamId;
            var sub = new CandleSubscriptionRequest(symbol, JsonConvert.SerializeObject(interval, new TimeFrameConverter(false)), handler) { StreamId = id };

            lock (subscriptionRequestsLock)
                subscriptionRequests.Add(sub);

            if (State != SocketState.Connected)
                return new CallResult<int>(id, null);

            log.Write(LogVerbosity.Debug, "Subscribing to candle updates for " + symbol);
            await SubscribeAndWait(sub).ConfigureAwait(false);
            return new CallResult<int>(id, null);
        }

        /// <summary>
        /// Unsubscribe from a specific channel using the id acquired when subscribing
        /// </summary>
        /// <param name="streamId">The channel id to unsubscribe from</param>
        /// <returns></returns>
        public async Task<CallResult<bool>> UnsubscribeFromChannel(int streamId)
        {
            log.Write(LogVerbosity.Debug, "Unsubscribing from channel " + streamId);
            SubscriptionRequest sub;
            lock(confirmedRequestLock)
                sub = confirmedRequests.SingleOrDefault(r => r.StreamId == streamId && r.ChannelId != null);

            if (sub != null)
            {
                if (State == SocketState.Connected)
                {
                    var result = await UnsubscribeAndWait(new UnsubscriptionRequest(sub.ChannelId.Value)).ConfigureAwait(false);
                    if (!result.Success)
                        return result;

                    lock (subscriptionRequestsLock)
                        subscriptionRequests.RemoveAll(r => r.StreamId == streamId);
                }
                else
                {
                    lock (subscriptionRequestsLock)
                        subscriptionRequests.RemoveAll(r => r.StreamId == streamId);
                }
            }
            else
            {
                bool found = false;
                lock(registrationsLock)
                {
                    if(registrations.Any(r => r.StreamId == streamId))
                    {
                        registrations.Remove(registrations.Single(r => r.StreamId == streamId));
                        found = true;
                    }
                }

                if (!found)
                {
                    log.Write(LogVerbosity.Warning, "No subscription found for channel id " + streamId);
                    return new CallResult<bool>(false, new ArgumentError("No subscription found for channel id " + streamId));
                }
            }
            
            return new CallResult<bool>(true, null);
        }
        #endregion
        #endregion

        #region private
        private void Create()
        {
            socket = SocketFactory.CreateWebsocket(log, baseAddress);
            socket.OnClose += SocketClosed;
            socket.OnError += SocketError;
            socket.OnOpen += SocketOpened;
            socket.OnMessage += SocketMessage;
        }

        private async Task<bool> Open()
        {
            bool connectResult = await socket.Connect().ConfigureAwait(false); 
            if (!connectResult)
            {
                log.Write(LogVerbosity.Warning, "Couldn't connect to socket");
                return false;
            }

            running = true;
            Task.Run(() => ProcessData());
            sendTask = Task.Run(() => ProcessSending());

            log.Write(LogVerbosity.Info, "Socket connection established");
            return true;
        }

        private async Task<CallResult<bool>> SubscribeAndWait(SubscriptionRequest request)
        {
            lock (outstandingSubscriptionRequestsLock)
                outstandingSubscriptionRequests.Add(request);

            Send(JsonConvert.SerializeObject(request));
            bool confirmed = false;
            await Task.Run(() =>
            {
                confirmed = request.ConfirmedEvent.WaitOne(subscribeResponseTimeout);

                lock (outstandingSubscriptionRequestsLock)
                    outstandingSubscriptionRequests.Remove(request);

                lock (confirmedRequestLock)
                    confirmedRequests.Add(request);
                log.Write(LogVerbosity.Debug, !confirmed ? "No confirmation received" : "Subscription confirmed");
            }).ConfigureAwait(false);

            return new CallResult<bool>(confirmed, confirmed ? null : new ServerError("No confirmation received"));
        }

        private async Task<CallResult<bool>> UnsubscribeAndWait(UnsubscriptionRequest request)
        {
            if (!CheckConnection())
                return new CallResult<bool>(false, new WebError("Can't unsubscribe when not connected"));

            lock (outstandingUnsubscriptionRequestsLock)
                outstandingUnsubscriptionRequests.Add(request);

            Send(JsonConvert.SerializeObject(request));
            bool confirmed = false;
            await Task.Run(() =>
            {
                confirmed = request.ConfirmedEvent.WaitOne(subscribeResponseTimeout);

                lock (outstandingUnsubscriptionRequestsLock)
                    outstandingUnsubscriptionRequests.Remove(request);

                lock (confirmedRequestLock)
                    confirmedRequests.RemoveAll(r => r.ChannelId == request.ChannelId);

                lock (subscriptionRequestsLock)
                    subscriptionRequests.Single(s => s.ChannelId == request.ChannelId).ResetSubscription();
                log.Write(LogVerbosity.Debug, !confirmed ? "No confirmation received" : "Unsubscription confirmed");
            }).ConfigureAwait(false);

            return new CallResult<bool>(confirmed, confirmed ? null : new ServerError("No confirmation received"));
        }

        private void SocketClosed()
        {
            log.Write(LogVerbosity.Debug, "Socket closed");
            if (!reconnect)
            {
                Dispose();
                return;
            }

            Task.Run(() =>
            {
                if (!lost)
                {
                    lost = true;
                    ConnectionLost?.Invoke();
                }

                Thread.Sleep(2000);
                StartInternal();
            });
        }

        private void SocketError(Exception ex)
        {
            log.Write(LogVerbosity.Error, $"Socket error: {ex?.GetType().Name} - {ex?.Message}");
        }

        private void SocketOpened()
        {
            log.Write(LogVerbosity.Debug, "Socket opened");
            Task.Run(async () =>
            {
                if (lost)
                {
                    ConnectionRestored?.Invoke();
                    lost = false;
                }

                Authenticate();
                await SubscribeUnsend().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private async Task SubscribeUnsend()
        {
            foreach (var sub in subscriptionRequests.ToList().Where(s => s.ChannelId == null))
            {
                int currentTry = 0;
                while (true)
                {
                    if (State != SocketState.Connected)
                        return;

                    currentTry++;
                    var subResult = await SubscribeAndWait(sub).ConfigureAwait(false);
                    if (subResult.Success)
                        break;

                    if (currentTry < 3)
                        log.Write(LogVerbosity.Warning, $"Failed to (re)sub {sub.GetType()}: {subResult.Error}, trying again");
                    else
                    {
                        log.Write(LogVerbosity.Error, $"Failed to (re)sub {sub.GetType()}: {subResult.Error}, tried {currentTry} times");
                        break;
                    }
                }
            }
        }

        private void SocketMessage(string msg)
        {
            log.Write(LogVerbosity.Debug, "Received message: " + msg);
            receivedMessages.Enqueue(msg);
            messageEvent.Set();
        }

        private void Send(string data)
        {
            toSendMessages.Enqueue(data);
            sendEvent.Set();
        }

        private void ProcessSending()
        {
            while (running)
            {
                sendEvent.WaitOne();
                if (!running)
                    break;

                while (toSendMessages.TryDequeue(out string data))
                {
                    log.Write(LogVerbosity.Debug, "Sending " + data);
                    socket.Send(data);
                }
            }
        }

        private void ProcessData()
        {
            while (running)
            {
                bool messageReceived = messageEvent.WaitOne(socketReceiveTimeout);
                if (!messageReceived)
                    StopInternal();

                if (!running)
                    break;

                while (receivedMessages.TryDequeue(out string dequeued))
                {
                    log.Write(LogVerbosity.Debug, "Processing " + dequeued);

                    try
                    {
                        var dataObject = JToken.Parse(dequeued);

                        if (dataObject is JObject)
                        {
                            var evnt = dataObject["event"].ToString();
                            if (evnt == "auth")
                            {
                                ProcessAuthenticationResponse(dataObject.ToObject<BitfinexAuthenticationResponse>());
                                continue;
                            }

                            if (evnt == "subscribed")
                            {
                                var channel = dataObject["channel"].ToString();
                                if (!subscriptionResponseTypes.ContainsKey(channel))
                                {
                                    log.Write(LogVerbosity.Warning, "Unknown response channel name: " + channel);
                                    continue;
                                }

                                SubscriptionResponse subResponse = (SubscriptionResponse)dataObject.ToObject(subscriptionResponseTypes[channel]);
                                var responseSubKeys = subResponse.GetSubscriptionKeys();
                                SubscriptionRequest pending = null;

                                foreach (var key in responseSubKeys)
                                {
                                    lock (outstandingSubscriptionRequestsLock)
                                        pending = outstandingSubscriptionRequests.SingleOrDefault(r => r.GetSubscriptionKey().ToLower() == key.ToLower());

                                    // If any of the keys match its a match
                                    if (pending != null)
                                        break;
                                }

                                if (pending == null)
                                {
                                    log.Write(LogVerbosity.Debug, "Couldn't find sub request for response");
                                    continue;
                                }

                                pending.ChannelId = subResponse.ChannelId;
                                pending.ConfirmedEvent.Set();
                                continue;
                            }

                            if (evnt == "unsubscribed")
                            {
                                UnsubscriptionRequest pending;
                                lock (outstandingUnsubscriptionRequestsLock)
                                    pending = outstandingUnsubscriptionRequests.SingleOrDefault(r => r.ChannelId == (int)dataObject["chanId"]);

                                if (pending == null)
                                {
                                    log.Write(LogVerbosity.Debug, "Received unsub confirmation, but no pending unsubscriptions");
                                    continue;
                                }
                                pending.ConfirmedEvent.Set();
                                continue;
                            }

                            if (evnt == "info")
                            {
                                if (dataObject["version"] != null)
                                {
                                    log.Write(LogVerbosity.Info, $"Websocket version: {dataObject["version"]}, platform status: {((int)dataObject["platform"]["status"] == 1? "operational": "maintance")}");
                                    continue;
                                }

                                var code = (int)dataObject["code"];
                                switch (code)
                                {
                                    case 20051:
                                        // reconnect
                                        log.Write(LogVerbosity.Info, "Received status code 20051, going to reconnect the websocket");
                                        Task.Run(() => StopInternal());
                                        continue;
                                    case 20060:
                                        // pause
                                        log.Write(LogVerbosity.Info, "Received status code 20060, pausing websocket activity");
                                        State = SocketState.Paused;
                                        SocketPaused?.Invoke();
                                        continue;
                                    case 20061:
                                        // resume
                                        log.Write(LogVerbosity.Info, "Received status code 20061, resuming websocket activity");
                                        Task.Run(async () =>
                                        {
                                            foreach (var sub in confirmedRequests.ToList())  
                                                await UnsubscribeAndWait(new UnsubscriptionRequest(sub.ChannelId.Value));

                                            await SubscribeUnsend();
                                            SocketResumed?.Invoke();
                                        });
                                        State = SocketState.Connected;
                                        continue;
                                }

                                log.Write(LogVerbosity.Warning, $"Received unknown status code: {code}, data: {dataObject}");
                            }
                        }
                        else
                        {
                            var channelId = (int)dataObject[0];
                            if (dataObject[1].ToString() == "hb")
                                continue;

                            SubscriptionRequest channelReg;
                            lock (confirmedRequestLock)
                                channelReg = confirmedRequests.SingleOrDefault(c => c.ChannelId == channelId);

                            if (channelReg != null)
                            {
                                channelReg.Handle((JArray)dataObject);
                                continue;
                            }

                            var messageType = dataObject[1].ToString();
                            HandleRequestResponse(messageType, (JArray)dataObject);

                            SubscriptionRegistration accountReg;
                            lock(registrationsLock)
                                accountReg = registrations.SingleOrDefault(r => r.UpdateKeys.Contains(messageType));

                            accountReg?.Handle((JArray)dataObject);
                        }
                    }
                    catch (Exception e)
                    {
                        log.Write(LogVerbosity.Error, $"Error in processing loop. {e.GetType()}, {e.Message}, {e.StackTrace}, message: {dequeued}");
                    }
                }
            }
        }

        private void HandleRequestResponse(string messageType, JArray dataObject)
        {
            if (messageType == "on")
            {
                // new order
                var orderResult = Deserialize<BitfinexOrder>(dataObject[2].ToString());
                if (!orderResult.Success)
                {
                    log.Write(LogVerbosity.Warning, "Failed to deserialize new order from stream: " + orderResult.Error);
                    return;
                }

                foreach (var pendingOrder in pendingOrders.ToList())
                {
                    var o = pendingOrder.Key;
                    if (o.Symbol == orderResult.Data.Symbol)
                    {
                        if ((o.Amount != null && (Math.Round(o.Amount.Value, 8) == Math.Round(orderResult.Data.AmountOriginal, 8)))
                            || (o.Price != null && Math.Round(o.Price.Value, 8) == Math.Round(orderResult.Data.Price, 8)))
                        {
                            pendingOrder.Value.Set(new CallResult<BitfinexOrder>(orderResult.Data, null));
                            break;
                        }
                    }
                }
            }
            else if (messageType == "oc")
            {
                // canceled order
                var orderResult = Deserialize<BitfinexOrder>(dataObject[2].ToString());
                if (!orderResult.Success)
                {
                    log.Write(LogVerbosity.Warning, "Failed to deserialize canceled order from stream: " + orderResult.Error);
                    return;
                }

                foreach (var pendingCancel in pendingCancels.ToList())
                {
                    var o = pendingCancel.Key;
                    if (o == orderResult.Data.Id)
                    {
                        pendingCancel.Value.Set(new CallResult<bool>(true, null));
                        break;
                    }
                }
            }

            else if (messageType == "ou")
            {
                // canceled order
                var orderResult = Deserialize<BitfinexOrder>(dataObject[2].ToString());
                if (!orderResult.Success)
                {
                    log.Write(LogVerbosity.Warning, "Failed to deserialize updated order from stream: " + orderResult.Error);
                    return;
                }

                foreach (var pendingUpdate in pendingUpdates.ToList())
                {
                    var o = pendingUpdate.Key;
                    if (o == orderResult.Data.Id)
                    {
                        pendingUpdate.Value.Set(new CallResult<bool>(true, null));
                        break;
                    }
                }
            }

            else if (messageType == "n")
            {
                // notification
                var dataArray = (JArray)dataObject[2];
                var noticationType = dataArray[1].ToString();
                if (noticationType == "on-req" || noticationType == "oc-req")
                {

                    var orderData = (JArray)dataArray[4];
                    var error = dataArray[6].ToString().ToLower() == "error";
                    var message = dataArray[7].ToString();
                    if (!error)
                        return;

                    if (dataArray[1].ToString() == "on-req")
                    {
                        // new order request
                        var orderAmount = decimal.Parse(orderData[6].ToString());
                        var orderType = (OrderType)orderData[8].ToObject(typeof(OrderType), new JsonSerializer() { Converters = { new OrderTypeConverter() } });
                        foreach (var pendingOrder in pendingOrders.ToList())
                        {
                            var o = pendingOrder.Key;
                            if (o.Amount == orderAmount && o.OrderType == orderType)
                            {
                                pendingOrder.Value.Set(new CallResult<BitfinexOrder>(null, new ServerError(message)));
                                break;
                            }
                        }
                    }
                    else if (dataArray[1].ToString() == "oc-req")
                    {
                        // cancel order request
                        var orderId = (long)orderData[0];
                        foreach (var pendingCancel in pendingCancels.ToList())
                        {
                            var o = pendingCancel.Key;
                            if (o == orderId)
                            {
                                pendingCancel.Value.Set(new CallResult<bool>(false, new ServerError(message)));
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void Authenticate()
        {
            if (authProvider == null || socket.IsClosed)
                return;

            authenticating = true;
            var n = Nonce;
            var authentication = new BitfinexAuthentication()
            {
                Event = "auth",
                ApiKey = authProvider.Credentials.Key,
                Nonce = n,
                Payload = "AUTH" + n
            };
            authentication.Signature = authProvider.Sign(authentication.Payload).ToLower();

            Send(JsonConvert.SerializeObject(authentication));
        }

        private bool CheckAuthentication()
        {
            if (authenticated)
                return true;

            if (!authenticating)
                return false;

            while(authenticating)
                Thread.Sleep(10);

            return authenticated;
        }

        private void ProcessAuthenticationResponse(BitfinexAuthenticationResponse response)
        {
            if (response.Status == "OK")
            {
                authenticated = true;
                log.Write(LogVerbosity.Info, "Authentication successful");
            }
            else
            {
                authenticated = false;
                log.Write(LogVerbosity.Warning, "Authentication failed: " + response.ErrorMessage);
            }
            authenticating = false;
        }

        private void GetSubscriptionResponseTypes()
        {
            subscriptionResponseTypes = new Dictionary<string, Type>();
            foreach (var t in typeof(SubscriptionResponse).Assembly.GetTypes())
            {
                if (typeof(SubscriptionResponse).IsAssignableFrom(t) && t.Name != typeof(SubscriptionResponse).Name)
                {
                    var attribute = (SubscriptionChannelAttribute)t.GetCustomAttributes(typeof(SubscriptionChannelAttribute), true)[0];
                    subscriptionResponseTypes.Add(attribute.ChannelName, t);
                }
            }
        }

        private void Configure(BitfinexSocketClientOptions options)
        {
            base.Configure(options);

            socketReceiveTimeout = options.SocketReceiveTimeout;
            subscribeResponseTimeout = options.SubscribeResponseTimeout;
            orderActionConfirmationTimeout = options.OrderActionConfirmationTimeout;
            baseAddress = options.BaseAddress;
        }

        private bool CheckConnection()
        {
            return socket != null && !socket.IsClosed;
        }

        private void Init()
        {
            receivedMessages = new ConcurrentQueue<string>();
            toSendMessages = new ConcurrentQueue<string>();
            messageEvent = new AutoResetEvent(true);
            sendEvent = new AutoResetEvent(true);
            
            outstandingSubscriptionRequests = new List<SubscriptionRequest>();
            outstandingUnsubscriptionRequests = new List<UnsubscriptionRequest>();
            confirmedRequests = new List<SubscriptionRequest>();
            pendingOrders = new ConcurrentDictionary<BitfinexNewOrder, WaitAction<BitfinexOrder>>();
            pendingCancels = new ConcurrentDictionary<long, WaitAction<bool>>();
            pendingUpdates = new ConcurrentDictionary<long, WaitAction<bool>>();
            authenticating = false;
            authenticated = false;

            if (subscriptionRequests == null)
                subscriptionRequests = new List<SubscriptionRequest>();

            foreach (var sub in subscriptionRequests)
                sub.ResetSubscription();

            if (registrations == null)
                registrations = new List<SubscriptionRegistration>();

            GetSubscriptionResponseTypes();
        }

        public override void Dispose()
        {
            base.Dispose();
            socket.Dispose();
        }

        #endregion
    }
}

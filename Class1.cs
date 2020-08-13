using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;

namespace OPCClient
{
    public class Client
    {
        private static ApplicationConfiguration _config;
        private static Session _session;
        private static bool _haveAppCertificate;
        // private static string _endpointUrl;             // "opc.tcp://192.168.11.90:49320";
        private static Subscription _subscription;
        private static List<MonitoredItem> _items;
        private CreateSubscription _callback;

        // Делегат для функции обратного вызова, выполняющаяся при получении нового значения от тега
        public delegate void CreateSubscription(MonitoredItem item, MonitoredItemNotificationEventArgs e);


        /// <summary>
        /// Подключение к OPC-серверу по его адресу. Выполняется асинхронно.
        /// </summary>
        /// <param name="ep">Адрес OPC-сервера</param>
        /// <returns></returns>
        public async Task Connect(string endpointUrl)
        {
            _config = new ApplicationConfiguration()
            {
                ApplicationName = "Console OPC-Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:localhost:OPCFoundation:SampleClient",
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/MachineDefault",
                        SubjectName = Utils.Format("CN={0}, DC={1}", "Console OPC-Client", Utils.GetHostName())
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/UA Applications",
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/UA Certificate Authorities",
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/RejectedCertificates",
                    },
                    NonceLength = 32,
                    AutoAcceptUntrustedCertificates = true
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };
            await _config.Validate(ApplicationType.Client);

            _haveAppCertificate = _config.SecurityConfiguration.ApplicationCertificate.Certificate != null;

            if (_haveAppCertificate && _config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
            {
                _config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
            }

            Uri endpointURI = new Uri(endpointUrl);
            var endpointCollection = DiscoverEndpoints(_config, endpointURI, 10);
            var selectedEndpoint = SelectUaTcpEndpoint(endpointCollection, _haveAppCertificate);
            var endpointConfiguration = EndpointConfiguration.Create(_config);
            var endpoint = new ConfiguredEndpoint(selectedEndpoint.Server, endpointConfiguration);
            endpoint.Update(selectedEndpoint);

            _session = await Session.Create(_config, endpoint, true, "Console OPC Client", 60000, null, null);
            _subscription = new Subscription(_session.DefaultSubscription) { PublishingInterval = 1000 };
        }

        /// <summary>
        /// Добавление тега в список подписки на получение новых значений
        /// </summary>
        /// <param name="name">Отображаемое имя тега</param>
        /// <param name="addr">Адрес тега</param>
        public void AddItem(string name, string addr)
        {
            if (_items == null)
            {
                _items = new List<MonitoredItem>();
            }

            // Создать переменню типа MonitoredItem
            var item = new MonitoredItem(_subscription.DefaultItem)
            {
                DisplayName = name,
                StartNodeId = addr
            };
            _items.Add(item);
        }

        /// <summary>
        /// Получить значение тега по его адресу
        /// </summary>
        /// <param name="nodeID">Адрес тега</param>
        /// <returns>Значение тега</returns>
        public DataValue ReadValue(string nodeID)
        {
            DataValue value = _session.ReadValue(nodeID);

            return value;
        }

        /// <summary>
        /// Запись значения тега по его адресу. В настоящее время проверено значение только для типа данных Single
        /// </summary>
        /// <param name="nodeID">Адрес тега</param>
        /// <param name="value">Новое значение тега</param>
        /// <returns>True - в случае успешной записи нового значения, False - при ошибке записи</returns>
        public static bool WriteValue(string nodeID, Single value)
        {
            WriteValueCollection writeValues = new WriteValueCollection();
            StatusCodeCollection results = new StatusCodeCollection();
            DiagnosticInfoCollection diagnosticInfos = new DiagnosticInfoCollection();

            WriteValue intWriteVal = new WriteValue();
            intWriteVal.NodeId = new NodeId(nodeID);
            intWriteVal.AttributeId = Attributes.Value;
            intWriteVal.Value = new DataValue(value);

            writeValues.Add(intWriteVal);
            // Call the Write service
            ResponseHeader responseHeader = _session.Write(null,
            writeValues,
            out results,
            out diagnosticInfos);

            bool res = StatusCode.IsGood(results[0].Code);

            return res;
        }


        private void Subscribe(CreateSubscription callback)
        {
            _callback = callback;
            _items.ForEach(i => i.Notification += OnNotification);
            _subscription.AddItems(_items);
            _session.AddSubscription(_subscription);
            _subscription.Create();
        }



        private void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            Console.WriteLine($"Принятый сертификат: {e.Certificate.Subject}");
            e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);
        }

        private EndpointDescriptionCollection DiscoverEndpoints(ApplicationConfiguration config, Uri discoveryUrl, int timeout)
        {
            // use a short timeout.
            EndpointConfiguration configuration = EndpointConfiguration.Create(config);
            configuration.OperationTimeout = timeout;

            using (DiscoveryClient client = DiscoveryClient.Create(
                discoveryUrl,
                EndpointConfiguration.Create(config)))
            {
                try
                {
                    EndpointDescriptionCollection endpoints = client.GetEndpoints(null);
                    ReplaceLocalHostWithRemoteHost(endpoints, discoveryUrl);
                    return endpoints;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Не удалось найти OPC-сервер по адресу: {discoveryUrl}");
                    Console.WriteLine($"Ошибка = {e.Message}");
                    throw e;
                }
            }
        }

        private EndpointDescription SelectUaTcpEndpoint(EndpointDescriptionCollection endpointCollection, bool haveCert)
        {
            EndpointDescription bestEndpoint = null;
            foreach (EndpointDescription endpoint in endpointCollection)
            {
                if (endpoint.TransportProfileUri == Profiles.UaTcpTransport)
                {
                    if (bestEndpoint == null ||
                        haveCert && (endpoint.SecurityLevel > bestEndpoint.SecurityLevel) ||
                        !haveCert && (endpoint.SecurityLevel < bestEndpoint.SecurityLevel))
                    {
                        bestEndpoint = endpoint;
                    }
                }
            }
            return bestEndpoint;
        }

        private void ReplaceLocalHostWithRemoteHost(EndpointDescriptionCollection endpoints, Uri discoveryUrl)
        {
            foreach (EndpointDescription endpoint in endpoints)
            {
                endpoint.EndpointUrl = Utils.ReplaceLocalhost(endpoint.EndpointUrl, discoveryUrl.DnsSafeHost);
                StringCollection updatedDiscoveryUrls = new StringCollection();
                foreach (string url in endpoint.Server.DiscoveryUrls)
                {
                    updatedDiscoveryUrls.Add(Utils.ReplaceLocalhost(url, discoveryUrl.DnsSafeHost));
                }
                endpoint.Server.DiscoveryUrls = updatedDiscoveryUrls;
            }
        }

        /// <summary>
        /// Оформл
        /// </summary>
        /// <param name="callback"></param>
        public void SubsOnNewData(CreateSubscription callback)
        {
            // Этот метод вызывается из главной программы (вызывающей)
            Subscribe(callback);
        }


        private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            _callback?.Invoke(item, e);
        }
    }
}

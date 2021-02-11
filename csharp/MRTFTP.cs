using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using MRT.Debug;
using System.Threading.Tasks;

namespace MRT.Network.FTP
{
	public enum AppActions { Start, Stop, Restart }
	public enum NetType { Client, Server }

	/// <summary>
	/// Точка доступа к функционалу FTP.
	/// </summary>
	public static class MRTFTP
	{
		#region PROPERTIES
		/// <summary>
		/// Адрес в локальной сети.
		/// </summary>
		public static string Ip
		{
			get
			{
				return NetworkUtils.GetLocalIP_1();
			}
		}
		public static int Port { get; private set; }
		public static NetType NetType { get; private set; }
		/// <summary>
		/// Истина, если сущетсвует активное подключение к клиенту или серверу.
		/// </summary>
		public static bool IsConnected { get; private set; }
		/// <summary>
		/// Истина, если запущен клиент или сервер, в т.ч. без наличия активного подключения.
		/// </summary>
		public static bool IsWorking { get; private set; }
		/// <summary>
		/// Объект удаленной директории. На сервере возвращает NULL. 
		/// </summary>
		public static RemoteDir remoteDir
		{
			get
			{
				if (_serverConnection == null) return null;
				return _serverConnection.RemoteDir;
			}
		}
		/// <summary>
		/// Объект локальной рабочей директории. На сервере возвращает директорию первого подключенного клиента.
		/// </summary>
		public static LocalDir localDir
		{
			get
			{
				if (NetType == NetType.Client)
				{
					return _serverConnection.LocalDir;
				}
				else
				{
					return _clients[0].dir;
				}
			}
		}
		#endregion
		#region EVENTS
		public static event Action<string> OnError = (_) => { };
		public static event Action OnServerConnected = () => { };
		public static event Action OnServerDisonnected = () => { };
		public static event Action<int, string> OnClientConnected = (id, ip) => { };
		public static event Action<int> OnClientDisconnected = (_) => { };
		public static event Action<int, string> OnSpecialCommand = (_, __) => { };
		public static event Action<string> OnFileTransferStart = (_) => { };
		public static event Action<string> OnFileTransferStop = (_) => { };
		#endregion
		#region PUBLIC VARS
		public static bool verboseDebugging = false;
		public static bool logToConsole = false;
		#endregion
		#region PRIVATE VARS
		private static TcpListener _listener;
		private static AsyncServerConnection _serverConnection;
		private static readonly List<AsyncClientConnection> _clients = new List<AsyncClientConnection>();
		private static readonly List<Task> _clientsTasks = new List<Task>();
		private static readonly object _lock = new object();
		private static List<ConcurrentQueue<Action>> _eventQueues = new List<ConcurrentQueue<Action>>();
		private static bool _listeningEvents = false;
		private static bool _needToStop = false;
		#endregion
		
		#region SERVER
		private static void StopServer()
		{
			if (NetType == NetType.Server && IsWorking)
			{
				if (_listener != null)
				{
					foreach (var item in _clients)
					{
						OnClientDisconnected(item.clientId);
						item.Cleanup();
					}
					_clients.Clear();
					_listener.Stop();
					_listener.Server.Close();
					_listener.Server.Dispose();
					_listener = null;
					GC.Collect();
				}
				IsWorking = false;
			}
		}
		private static Task StartServerAsync(int port, string password = "Heil,MRT!")
		{
			return Task.Run(async () =>
			{
				TcpListener tcpListener = TcpListener.Create(port);
				tcpListener.Start();
				while (true)
				{
					TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync();
					Task task = StartHandleConnectionAsync(tcpClient, password);
					if (task.IsFaulted) await task; // throw all exeptions in current context
				}
			});
		}
		private static async Task StartHandleConnectionAsync(TcpClient client, string password)
		{
			AsyncClientConnection clientConnection = new AsyncClientConnection();
			clientConnection.truePassword = password;
			_eventQueues.Add(clientConnection.eventsQueue);
			clientConnection.verboseDebugging = verboseDebugging;
			clientConnection.logToConsole = logToConsole;
			clientConnection.OnClientError += OnError;
			clientConnection.OnClientDisconnected += OnClientDisconnected;
			clientConnection.OnClientConnected += OnClientConnected;
			clientConnection.OnFileTransitionStart += OnFileTransferStart;
			clientConnection.OnFileTransitionStop += OnFileTransferStop;
			_clients.Add(clientConnection);
			clientConnection.clientId = _clients.Count - 1;
			Task connectionTask = clientConnection.HandleConnectionAsync(client);
			lock (_lock)
			{
				_clientsTasks.Add(connectionTask);
			}
			try
			{
				await connectionTask;
			}
			catch (Exception e)
			{
				OnError(e.Message);
				OnClientDisconnected(clientConnection.clientId);
				if (verboseDebugging) XDebug.Log("[Server] Closing client connection " + e.Message, XDebug.DebugColors.Yellow, logToConsole);
				throw;
			}
			finally
			{
				await Task.Delay(101);
				clientConnection.OnClientError -= OnError;
				clientConnection.OnClientDisconnected -= OnClientDisconnected;
				clientConnection.OnClientConnected -= OnClientConnected;
				clientConnection.OnFileTransitionStart -= OnFileTransferStart;
				clientConnection.OnFileTransitionStop -= OnFileTransferStop;
				//_eventQueues.Remove(clientConnection.eventsQueue);
				lock (_lock)
				{
					_clientsTasks.Remove(connectionTask);
					_clients.Remove(clientConnection);
				}
			}
			if (verboseDebugging) XDebug.Log($"[Server] Connection with client [{clientConnection.clientId}] closed!", XDebug.DebugColors.Yellow, logToConsole);
		}
		#endregion
		#region CLIENT
		private static async void StartClientAsync(string ip, int port)
		{
			if (!IsWorking)
			{
				NetType = NetType.Client;
				IsWorking = true;
				_serverConnection = new AsyncServerConnection(ip, "mrt.client", "Heil,MRT!", port);
				_serverConnection.verboseDebugging = verboseDebugging;
				_serverConnection.logToConsole = logToConsole;
				_serverConnection.OnServerError += OnError;
				_serverConnection.OnServerConnected += OnServerConnected;
				_serverConnection.OnServerDisconnected += OnServerDisonnected;
				_serverConnection.OnSpecialCommand += (s) => { OnSpecialCommand(-1, s); };
				_serverConnection.OnFileTransitionStart += OnFileTransferStart;
				_eventQueues.Add(_serverConnection.eventsQueue);
				await _serverConnection.Login();
			}
		}
		private static async void StopClientAsync()
		{
			if (IsWorking && NetType == NetType.Client)
			{
				await _serverConnection.Close();
				_serverConnection.OnServerError -= OnError;
				_serverConnection.OnServerConnected -= OnServerConnected;
				_serverConnection.OnServerDisconnected -= OnServerDisonnected;
				_serverConnection.OnFileTransitionStart -= OnFileTransferStart;
				_serverConnection.OnSpecialCommand -= (s) => { OnSpecialCommand(-1, s); };
				_serverConnection = null;
				GC.Collect();
			}
		}
		#endregion

		#region PUBLIC METHODS
		/// <summary>
		/// Запускает работу сети в качестве сервера.
		/// </summary>
		/// <param name="port">Порт, на котором сервер ожидает подключения</param>
		public static void StartAsServer(int port)
		{
			BeginListeningEvents();
			Port = port;
			if (verboseDebugging) XDebug.Log("Starting MRTFTP server...", XDebug.DebugColors.Yellow, logToConsole);
			StartServerAsync(port);
		}
		/// <summary>
		/// Начинает работу сети в качестве клиента и подключется к серверу.
		/// </summary>
		/// <param name="ip">Адрес сервера для подключения</param>
		/// <param name="port">Порт сервера</param>
		public static void StartAsClient(string ip, int port)
		{
			BeginListeningEvents();
			Port = port;
			if (verboseDebugging) XDebug.Log("Starting MRTFTP client...", XDebug.DebugColors.Yellow, logToConsole);
			StartClientAsync(ip, port);
		}
		/// <summary>
		/// Останавливает работу сети. Общая функция для клиента и сервера.
		/// </summary>
		public static void Stop()
		{
			if (IsWorking)
			{
				if (NetType == NetType.Client)
				{
					StopClientAsync();
				}
				else
				{
					StopServer();
				}
				_needToStop = true;
			}
		}
		/// <summary>
		/// Отправляет команду на подключенные устройства. Общая функция для клиента и сервера.
		/// </summary>
		/// <param name="cmd">Команда для отправки</param>
		/// <param ftpCommand="false">Интерпретировать как FTP команду?</param>
		/// <param id="0">id клиента на который будет отправлена команда. Только для сервера</param>
		/// <returns>Ответ на команду. Если команда не интерпертировалась как FTP - возвращает "ОК"</returns>
		public static async Task<string> SendCommand(string cmd, bool ftpCommand = false, int id = 0)
		{
			if (IsWorking)
			{
				if (NetType == NetType.Client)
				{
					if (IsConnected)
					{
						if (ftpCommand) return await _serverConnection.SendCommandAsync(cmd);
						else await _serverConnection.SendSpecialCommand(cmd);
					}
				}
				else
				{
					if (_clients.Count >= id && _clients[id] != null)
					{
						await _clients[id].SendCommand(cmd, ftpCommand);
					}
				}
				return "OK";
			}
			return "NO CONNECTION";
		}
		#endregion

		#region EVENTS QUEUES
		private static async void BeginListeningEvents()
		{
			if (_listeningEvents) return;
			_listeningEvents = true;
			while (!_needToStop)
			{
				await Task.Delay(16);
				for (int i = 0; i < _eventQueues.Count; i++)
				{
					for (int j = 0; j < _eventQueues[i].Count; j++)
					{
						if (_eventQueues[i].TryDequeue(out Action action))
						{
							action();
						}
					}
				}
			}
			_needToStop = false;
		}
		#endregion
	}
}

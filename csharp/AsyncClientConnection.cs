using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MRT.Debug;

namespace MRT.Network.FTP
{
	class AsyncClientConnection : IDisposable
	{
		#region ENUMS
		public enum DataConnectionType { Active, Passive }
		private enum TransferType
		{
			Ascii,
			Ebcdic,
			Byte,
			Local
		}
		private enum FormatControlType
		{
			NonPrint,
			Telnet,
			CarriageControl
		}
		private enum FileStructureType
		{
			File,
			Record,
			Page
		}
		#endregion
		#region EVENTS
		public event Action<int> OnClientDisconnected = (_) => { };
		public event Action<int, string> OnClientConnected = (id, ip) => { };
		public event Action<string> OnClientError = (_) => { };
		public event Action<AppActions> OnPanelAppAction = (_) => { };
		public event Action<AppActions> OnLauncherAppAction = (_) => { };
		public event Action<string> OnCommandFromClient = (_) => { };
		public event Action<string> OnFileTransitionStart = (_) => { };
		public event Action<string> OnFileTransitionStop = (_) => { };
		#endregion
		#region PROPERTIES
		/// <summary>
		/// Объект локальной рабочей директории.
		/// </summary>
		public LocalDir dir { get; private set; } = new LocalDir(null);
		public long LastPing { get; private set; }
		#endregion
		#region PUBLIC VARS
		public string truePassword = "Heil,MRT!";
		public bool verboseDebugging = true;
		public bool logToConsole = true;
		public int clientId = -1;
		public ConcurrentQueue<Action> eventsQueue = new ConcurrentQueue<Action>();
		#endregion
		#region PRIVATE VARS
		private int DATA_BUFFER_SIZE = 1024 * 100;
		private NetworkStream _clientSream;
		private NetworkStream _dataSream;
		private StreamWriter _clientWriter;
		private StreamReader _clientReader;
		private string _username;
		private TcpListener _passiveListener;
		private TcpClient _controlClient;
		private DataConnectionType _connectionType;
		private TransferType _transferType;
		private string _renameFrom;
		private string _copyFrom;
		private TcpListener _specialListener;
		private NetworkStream _specialStream;
		private StreamWriter _specialStreamWriter;
		private StreamReader _specialStreamReader;
		private Task _specialTask;
		private TcpListener _pingListener;
		private NetworkStream _pingStream;
		private Task _pingTask;
		private bool _canSendResp = true;
		private bool _needToStop = false;
		#endregion

		#region PUBLIC METHODS
		/// <summary>
		/// Запускает поддержку соединения с клиентом.
		/// </summary>
		public Task HandleConnectionAsync(TcpClient tcpClient)
		{
			_controlClient = tcpClient;
			dir = new LocalDir(null);
			return Task.Run(async () =>
			{
				using (_clientSream = tcpClient.GetStream())
				{
					_clientWriter = new StreamWriter(_clientSream);
					_clientReader = new StreamReader(_clientSream);
					await WriteToClientAsync("220 Connection established.");
					eventsQueue.Enqueue(() => { OnClientConnected(clientId, tcpClient.Client.RemoteEndPoint.ToString().Replace("[::ffff:", "").Replace("]", "")); });
					//OnClientConnected.Invoke(clientId);
					while (!_needToStop)
					{
						string request = await _clientReader.ReadLineAsync();
						if (string.IsNullOrEmpty(request))
						{
							eventsQueue.Enqueue(() =>
							{
								OnClientDisconnected(clientId);
							});
							Cleanup();
							break;
						}
						if (verboseDebugging) XDebug.Log($"[FTPServer] Recieved from client[{clientId}]: {request.Replace("\r\n", "")}", XDebug.DebugColors.Default, logToConsole);
						string response = await ExecuteCommandAsync(request);

						if (!string.IsNullOrEmpty(response)) await WriteToClientAsync(response);
					}
				}
			});
		}
		/// <summary>
		/// Отправляет команду на клиент.
		/// </summary>
		/// <param name="msg">Команда</param>
		/// <param name="ftpCommand">Интерпретировать как FTP команду?</param>
		public async Task SendCommand(string msg, bool ftpCommand = false)
		{
			if (ftpCommand)
			{
				await WriteToClientAsync(msg);
			}
			else
			{
				await _specialStreamWriter.WriteAsync(msg);
				await _specialStreamWriter.FlushAsync();
			}
		}
		/// <summary>
		/// Очищает все ресурсы и останавливает потоки.
		/// </summary>
		public void Cleanup()
		{
			if (verboseDebugging) XDebug.Log("[FTPServer] Cleanup", XDebug.DebugColors.Yellow, logToConsole);
			_needToStop = true;
			if (_pingListener != null)
			{
				_pingListener.Stop();
				_pingListener = null;
			}
			if (_pingStream != null)
			{
				_pingStream.Close();
				_pingStream.Dispose();
			}
			if (_passiveListener != null)
			{
				_passiveListener.Stop();
				_passiveListener = null;
			}
			if (_specialListener != null)
			{
				_specialListener.Stop();
				_specialListener = null;
			}
			if (_specialStreamReader != null)
			{
				_specialStreamReader.Close();
				_specialStreamReader.Dispose();
			}
			if (_specialStreamWriter != null)
			{
				_specialStreamWriter.Close();
				_specialStreamWriter.Dispose();
			}
			if (_specialStream != null)
			{
				_specialStream.Close();
				_specialStream.Dispose();
			}
		}
		public void Dispose()
		{
			Cleanup();
		}
		#endregion
		#region PRIVATE METHODS
		#region FTP spec
		private string UserName(string userName) //+
		{
			XDebug.Log($"Client username: {userName}");
			if (userName == "mrt.client")
			{
				//_fromMRTClient = true;
				_username = userName;
				return "331 Правильный логин. HEIL MRT!";
			}
			else if (userName == "other.client")
			{
				//_fromMRTClient = false;
				_username = userName;
				return "331 Правильный логин, но лучше используй MRT клиент!";
			}
			else
			{
				_username = "";
				return "530 Need account for login.";
			}
		}
		private string Password(string password) //+
		{
			if (!string.IsNullOrEmpty(_username) && password == truePassword)
			{
				return "230 User logged in, proceed.";
			}
			else return "530 Сначала пароль правильный введи, потом поговорим.";
		}
		private string Account(string userName)
		{
			return "502 Command not implemented. " + userName;
		}
		private string ChangeWorkingDirecory(string path) //+
		{
			if (!string.IsNullOrEmpty(path) && !string.IsNullOrWhiteSpace(path))
			{
				if (dir.DownDirectory(path))
				{
					return "200 OK.";
				}
				else
				{
					return "550 Directory not found.";
				}
			}
			else
			{
				return "501 Invalid arguments.";
			}
		}
		private string ChangeToParentDirectory()
		{
			dir.UpDirectory();
			return $"200 OK. \"{dir.ActualPath}\"";
		}
		private string StructureMount(string _)
		{
			return "502 Command not implemented.";
		}
		private string Reinitialize(string _)
		{
			return "502 Command not implemented.";
		}
		private string Logout(string _)
		{
			return "502 Command not implemented.";
		}
		private string DataPort(string _) //+
		{
			string[] args = _.Split(',');

			byte[] b_ip = new byte[4];
			byte[] b_port = new byte[2];

			b_ip[0] = byte.Parse(args[0]);
			b_ip[1] = byte.Parse(args[1]);
			b_ip[2] = byte.Parse(args[2]);
			b_ip[3] = byte.Parse(args[3]);

			b_port[0] = byte.Parse(args[4]);
			b_port[1] = byte.Parse(args[5]);

			if (BitConverter.IsLittleEndian)
			{
				//Array.Reverse(b_port);
			}

			//_dataPort = BitConverter.ToInt16(b_port, 0);
			//int _dataPort = b_port[0] * 256 + b_port[1];
			//string _dataIp = $"{b_ip[0]}.{b_ip[1]}.{b_ip[2]}.{b_ip[3]}";

			//_dataEndPoint = new IPEndPoint(IPAddress.Parse(_dataIp), _dataPort);
			return "200 OK.";
		}
		private string Passive(string _) //+
		{
			IPAddress localAddress = ((IPEndPoint)_controlClient.Client.LocalEndPoint).Address.MapToIPv4();
			if (_passiveListener == null) _passiveListener = new TcpListener(localAddress, 0);
			_passiveListener.Start();

			IPEndPoint localEndpoint = ((IPEndPoint)_passiveListener.LocalEndpoint);
			byte[] adress = localEndpoint.Address.GetAddressBytes();
			short port = (short)localEndpoint.Port;
			byte[] portArray = BitConverter.GetBytes(port);
			if (BitConverter.IsLittleEndian) Array.Reverse(portArray);
			_connectionType = DataConnectionType.Passive;
			XDebug.Log($"Entering passive mode ip: {localEndpoint.Address} port: {localEndpoint.Port}");
			return $"227 Entering passive mode ({adress[0]},{adress[1]},{adress[2]},{adress[3]},{portArray[0]},{portArray[1]})";
		}
		private string RepresentationType(string _) //+-
		{
			string[] args = _.Split(' ');
			string response;
			switch (args[0])
			{
				case "A":
					_transferType = TransferType.Ascii;
					response = "200 OK.";
					break;
				case "I":
					_transferType = TransferType.Byte;
					response = "200 OK.";
					break;
				case "E":
				case "L":
				default:
					response = "504 Command not implemented for that parameter.";
					break;
			}
			if (args.Length == 2 && !string.IsNullOrEmpty(args[1]))
			{
				switch (args[1])
				{
					case "N":
						response = "200 OK.";
						break;
					case "T":
					case "C":
					default:
						response = "504 Command not implemented for that parameter.";
						break;
				}
			}
			return response;
		}
		private string FileStructure(string _)
		{
			return "502 Command not implemented.";
		}
		private string TransferMode(string _)
		{
			return "502 Command not implemented.";
		}
		private async Task<string> Retrieve(string path) //+
		{
			path = Path.Combine(dir.ActualPath, path);
			if (!File.Exists(path)) return "550 File not found.";
			eventsQueue.Enqueue(() => { OnFileTransitionStart(path); });
			if (_connectionType == DataConnectionType.Active)
			{

			}
			else
			{
				await WriteToClientAsync($"150 Opening {_connectionType} mode data transfer for LIST.");
				TcpClient tcpClient = await _passiveListener.AcceptTcpClientAsync();
				using (_dataSream = tcpClient.GetStream())
				{
					await NetworkUtils.SendFileToNetworkStream(path, _dataSream, DATA_BUFFER_SIZE);
					_dataSream.Close();
				}
			}
			eventsQueue.Enqueue(() => { OnFileTransitionStop(path); });
			return "226 Closing data connection, file transfer successful";
		}
		private async Task<string> Store(string path) //+
		{
			path = Path.Combine(dir.ActualPath, path);
			eventsQueue.Enqueue(() => { OnFileTransitionStart(path); });
			if (_connectionType == DataConnectionType.Active)
			{

			}
			else
			{
				await WriteToClientAsync($"150 Opening {_connectionType} mode data transfer for LIST.");
				TcpClient tcpClient = await _passiveListener.AcceptTcpClientAsync();
				using (_dataSream = tcpClient.GetStream())
				{
					await NetworkUtils.WriteFileFromNetworkStream(path, _dataSream, DATA_BUFFER_SIZE);
					_dataSream.Close();
				}
			}
			eventsQueue.Enqueue(() => { OnFileTransitionStop(path); });
			return "226 Closing data connection, file transfer successful";
		}
		private string StoreUnique(string _)
		{
			return "502 Command not implemented.";
		}
		private string Append(string _)
		{
			return "502 Command not implemented.";
		}
		private string Allocate(string _)
		{
			return "502 Command not implemented.";
		}
		private string Restart(string _)
		{
			return "502 Command not implemented.";
		}
		private string RenameFrom(string name) //+
		{
			if (string.IsNullOrEmpty(name)) return "501 Invalid arguments";
			string fullname = Path.Combine(dir.ActualPath, name);
			Console.WriteLine(fullname);
			if (Directory.Exists(fullname) || File.Exists(fullname))
			{
				_renameFrom = fullname;
				return "350 OK. Waiting for RNTO...";
			}
			else
			{
				return "550 Directory not found.";
			}
		}
		private string RenameTo(string name) //+
		{
			if (string.IsNullOrEmpty(name)) return "501 Invalid arguments";
			string fullName = Path.Combine(dir.ActualPath, name);
			if (Directory.Exists(fullName) || File.Exists(fullName)) return "553 Directory with the same name already exists.";
			bool _dir = !Path.HasExtension(fullName);
			if (_dir)
			{
				try
				{
					Directory.Move(_renameFrom, fullName);
					return "250 OK.";
				}
				catch
				{
					return "553 Directory unavailable.";
				}
			}
			else
			{
				try
				{
					File.Move(_renameFrom, fullName);
					return "250 OK.";
				}
				catch
				{
					return "553 File unavailable.";
				}
			}
		}
		private string Abort(string _)
		{
			return "502 Command not implemented.";
		}
		private string Delete(string path) //+
		{
			path = path.Trim();
			if (CheckLocalFile(path, out path))
			{
				try
				{
					File.Delete(path);
					return $"250 File {path} removed.";
				}
				catch
				{
					return $"450 File {path} unavailable.";
				}
			}
			else return "550 File not found.";
		}
		private string RemoveDirectory(string path) //+
		{
			path = path.Trim();
			if (CheckLocalFolder(path, out path))
			{
				try
				{
					Directory.Delete(path, true);
					return $"250 Directory {path} removed.";
				}
				catch
				{
					return "550 Directory unavailable.";
				}

			}
			else return "550 Directory not found.";
		}
		private string MakeDirectory(string dirName) //+
		{
			if (!string.IsNullOrEmpty(dirName) && !string.IsNullOrWhiteSpace(dirName))
			{
				dir.CreateDirectory(dirName);
				return $"257 Directory {dirName} created.";
			}
			else
			{
				return "501 Invalid arguments.";
			}
		}
		private string PrintWorkingDirectory(string _) //+
		{
			//var state = new DataConnectionOperation { Arguments = dir.ActualPath, Operation = ListOperation };
			//SetupDataConnectionOperation(state);
			return $"257 \"{dir.ActualPath}\" is current directory.";
		}
		private async Task<string> List(string path) //+
		{
			if (string.IsNullOrEmpty(path)) path = dir.ActualPath;
			if (_connectionType == DataConnectionType.Active)
			{

			}
			else
			{
				await WriteToClientAsync($"150 Opening {_connectionType} mode data transfer for LIST.");
				TcpClient tcpClient = await _passiveListener.AcceptTcpClientAsync();
				using (_dataSream = tcpClient.GetStream())
				using (StreamWriter sw = new StreamWriter(_dataSream))
				{
					IEnumerable<string> directories = Directory.EnumerateDirectories(path);
					foreach (string dir in directories)
					{
						DirectoryInfo d = new DirectoryInfo(dir);
						string date = d.LastWriteTime.ToString("MM dd yyyy");
						string line = string.Format("drwxr-xr-x 1 mrt ftp {0} {1} {2}", "4096", date, d.Name);
						await sw.WriteLineAsync(line);
					}

					IEnumerable<string> files = Directory.EnumerateFiles(path);
					foreach (string file in files)
					{
						FileInfo f = new FileInfo(file);
						string date = f.LastWriteTime.ToString("MM dd yyyy");
						string line = string.Format("-rw-r--r-- 1 mrt ftp {0} {3} {1} {2}", f.Length, date, f.Name, f.Length);
						await sw.WriteLineAsync(line);
					}
					await sw.FlushAsync();
					sw.Close();
					_dataSream.Close();
				}
			}
			return "226 Transfer complete.";
		}
		private async Task<string> NameList(string path)
		{
			if (string.IsNullOrEmpty(path)) path = dir.ActualPath;
			if (_connectionType == DataConnectionType.Active)
			{

			}
			else
			{
				await WriteToClientAsync($"150 Opening {_connectionType} mode data transfer for LIST.");
				TcpClient tcpClient = await _passiveListener.AcceptTcpClientAsync();
				using (_dataSream = tcpClient.GetStream())
				using (StreamWriter sw = new StreamWriter(_dataSream))
				{
					IEnumerable<string> directories = Directory.EnumerateDirectories(path);
					foreach (string dir in directories)
					{
						DirectoryInfo d = new DirectoryInfo(dir);
						await sw.WriteLineAsync(d.Name);
					}

					IEnumerable<string> files = Directory.EnumerateFiles(path);
					foreach (string file in files)
					{
						FileInfo f = new FileInfo(file);
						await sw.WriteLineAsync(f.Name);
					}
					await sw.FlushAsync();
					sw.Close();
					_dataSream.Close();
				}
			}
			return "226 Transfer complete.";
		}
		private string SiteParameters(string _)
		{
			return "502 Command not implemented.";
		}
		private string System(string _)
		{
			return "215 UNIX Type: L8";
		}
		private string Status(string _)
		{
			return "502 Command not implemented.";
		}
		private string Size(string name)
		{
			if (string.IsNullOrEmpty(name)) return "501 Invalid arguments.";
			FileInfo fi = new FileInfo(Path.Combine(dir.ActualPath, name));
			if (!fi.Exists) return "550 File not found.";
			return $"213 {fi.Length}";
		}
		private string Help(string _)
		{
			return "502 Command not implemented.";
		}
		private string Noop(string _)
		{
			return "502 Command not implemented.";
		}
		private string Feat(string _) //+
		{
			return
				@"211-
UTF8
CWD
CDUP
PORT
PASV
TYPE
RETR
STOR
RNFR
RNTO
RMD
MKD
PWD
LIST
211 END.
";
		}
		private string Opts(string args) //+
		{
			if (args.ToUpper().Trim() == "UTF8 ON")
			{
				//_currEncoding = Encoding.UTF8;
				return "200 UTF8 ON.";
			}
			return "451 Invalid arguments.";
		}
		#endregion
		#region MRTFTP spec
		private string RestartPanel() //+
		{
			OnPanelAppAction.Invoke(AppActions.Restart);
			return "200 Restarting panel...";
		}
		private string RestartLauncher() //+
		{
			OnLauncherAppAction.Invoke(AppActions.Restart);
			return "200 Restarting launcher...";
		}
		private string StopPanel() //+
		{
			OnPanelAppAction.Invoke(AppActions.Stop);
			return "200 Stopping panel...";
		}
		private string StopLauncher() //+
		{
			OnLauncherAppAction.Invoke(AppActions.Stop);
			return "200 Stopping launcher...";
		}
		private string StartPanel() //+
		{
			OnPanelAppAction.Invoke(AppActions.Start);
			return "200 Strating panel...";
		}
		private string GoToStreamingAssets() //+
		{
			if (dir.StringNameOfActivePanel != "DIR_NOT_FOUND")
			{
				dir.GoToStreamingAssets();
				return $"200 Directory changed to #{dir.ActualPath}";
			}
			else
			{
				return "550 Directory not found.";
			}
		}
		private string RemoveIndex() //+
		{
			int result = dir.RemoveIndex();
			if (result == -1)
			{
				return $"450 {result} Index file unavailable.";
			}
			else if (result == 0)
			{
				return $"550 {result} No index in current directory.";
			}
			else
			{
				return $"250 {result} First index removed.";
			}
		}
		private string RemoveVideo() //+
		{
			int result = dir.RemoveVideo();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No video files found.";
			}
			else
			{
				return $"250 {result} First video file removed.";
			}
		}
		private string RemoveImage() //+
		{
			int result = dir.RemoveImage();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No image files found.";
			}
			else
			{
				return $"250 {result} First image file removed.";
			}
		}
		private string RemoveAudio() //+
		{
			int result = dir.RemoveAudio();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No audio files found.";
			}
			else
			{
				return $"250 {result} First audio file removed.";
			}
		}
		private string RemoveVideos() //+
		{
			int result = dir.RemoveVideos();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No video files found.";
			}
			else
			{
				return $"250 {result} All videos removed.";
			}
		}
		private string RemoveImages() //+
		{
			int result = dir.RemoveImages();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No image files found.";
			}
			else
			{
				return $"250 {result} All images removed.";
			}
		}
		private string RemoveAudios() //+
		{
			int result = dir.RemoveAudios();
			if (result == -1)
			{
				return $"450 {result} Оne of the files is not available.";
			}
			else if (result == 0)
			{
				return $"550 {result} No audio files found.";
			}
			else
			{
				return $"250 {result} All audios removed.";
			}
		}
		private string CreateIndex(string indexText) //+
		{
			dir.WriteIndex(indexText, SystemLanguage.Russian);
			return "200 Index created.";
		}
		private string ClearDirectory(string path)
		{
			if (string.IsNullOrEmpty(path)) dir.ClearDirectory(dir.ActualPath);
			else
			{
				if (Directory.Exists(path))
				{
					dir.ClearDirectory(path);
				}
				else
				{
					return "550 Directory not found.";
				}
			}
			return "200 Current directory cleaned.";
		}
		private string CopyFrom(string path)
		{
			if (!File.Exists(path) && !Directory.Exists(path))
			{
				_copyFrom = null;
				return "550 Path doesn't exists.";
			}
			_copyFrom = path;
			return "200 OK.";
		}
		private async Task<string> CopyTo(string path)
		{
			if (string.IsNullOrEmpty(_copyFrom)) return "550 Path doesn't exists.";
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return "550 Path doesn't exists.";
			bool isFile = Path.HasExtension(_copyFrom);
			if (isFile)
			{
				File.Copy(_copyFrom, path);
			}
			else
			{
				await dir.CopyDirectoryAsync(_copyFrom, path);
			}
			return "200 OK.";
		}
		/// <summary>
		/// Получает дату и время последнего редактирования файла/директории в формате "статус месяц день год часы минуты секунды"
		/// </summary>
		/// <param name="path">Путь к файлу/директории</param>
		/// <returns>Дата и время последнего редактирования в формате "статус месяц день год часы минуты секунды".
		/// Если файл или директория не найдены - "550 Path doesn't exists."
		/// </returns>
		private string GetDateTime(string path)
		{
			if (LocalDir.IsRelativePath(path)) path = Path.Combine(dir.ActualPath, path);
			DateTime dt;
			if (File.Exists(path))
			{
				dt = new FileInfo(path).LastWriteTime;
			}
			else if (Directory.Exists(path))
			{
				dt = new DirectoryInfo(path).LastWriteTime;
			}
			else
			{
				return "550 Path doesn't exists.";
			}
			StringBuilder sb = new StringBuilder("200 ");
			sb.Append(dt.Month);
			sb.Append(' ');
			sb.Append(dt.Day);
			sb.Append(' ');
			sb.Append(dt.Year);
			sb.Append(' ');
			sb.Append(dt.Hour);
			sb.Append(' ');
			sb.Append(dt.Minute);
			sb.Append(' ');
			sb.Append(dt.Second);
			return sb.ToString();
		}
		private string GetPanelName()
		{
			string result = dir.StringNameOfActivePanel;
			if (result == "DIR_NOT_FOUND") return "550 DIR_NOT_FOUND";
			return "200 " + result;
		}
		private string StartPingStream()
		{
			IPAddress localAddress = ((IPEndPoint)_controlClient.Client.LocalEndPoint).Address.MapToIPv4();
			if (_pingListener == null) _pingListener = new TcpListener(localAddress, 0);
			_pingListener.Start();

			IPEndPoint localEndpoint = ((IPEndPoint)_pingListener.LocalEndpoint);
			byte[] adress = localEndpoint.Address.GetAddressBytes();
			short port = (short)localEndpoint.Port;
			byte[] portArray = BitConverter.GetBytes(port);
			if (BitConverter.IsLittleEndian) Array.Reverse(portArray);
			XDebug.Log($"Start listenenig Ping: {localEndpoint.Address} port: {localEndpoint.Port}");
			return $"200 Start listenenig Ping ({adress[0]},{adress[1]},{adress[2]},{adress[3]},{portArray[0]},{portArray[1]})";
		}
		private string HandlePingStream()
		{
			_pingTask = Task.Run(HandlePingConnection);
			return "200 OK.";
		}
		private string StartSpecialStream()
		{
			IPAddress localAddress = ((IPEndPoint)_controlClient.Client.LocalEndPoint).Address.MapToIPv4();
			if (_specialListener == null) _specialListener = new TcpListener(localAddress, 0);
			_specialListener.Start();

			IPEndPoint localEndpoint = ((IPEndPoint)_specialListener.LocalEndpoint);
			byte[] adress = localEndpoint.Address.GetAddressBytes();
			short port = (short)localEndpoint.Port;
			byte[] portArray = BitConverter.GetBytes(port);
			if (BitConverter.IsLittleEndian) Array.Reverse(portArray);
			XDebug.Log($"Start listenenig Special: {localEndpoint.Address} port: {localEndpoint.Port}");
			return $"200 Start listenenig Special ({adress[0]},{adress[1]},{adress[2]},{adress[3]},{portArray[0]},{portArray[1]})";
		}
		private string HandleSpecialStream()
		{
			_specialTask = Task.Run(HandleSpecialConnection);
			return "200 OK.";
		}
		private string SetDataBufferSize(string args)
		{
			if (string.IsNullOrEmpty(args) || !int.TryParse(args, out int t)) return "501 Invalid arguments.";
			else
			{
				DATA_BUFFER_SIZE = t;
			}
			return "200 OK.";
		}
		private async Task<string> ExtendedList(string path)
		{
			if (string.IsNullOrEmpty(path)) path = dir.ActualPath;
			if (_connectionType == DataConnectionType.Active)
			{

			}
			else
			{
				await WriteToClientAsync($"150 Opening {_connectionType} mode data transfer for LIST.");
				TcpClient tcpClient = await _passiveListener.AcceptTcpClientAsync();
				using (_dataSream = tcpClient.GetStream())
				using (StreamWriter sw = new StreamWriter(_dataSream))
				{
					IEnumerable<string> directories = Directory.EnumerateDirectories(path);
					foreach (string dir in directories)
					{
						DirectoryInfo d = new DirectoryInfo(dir);
						string date = d.LastWriteTime.ToString("MM dd yyyy");
						string line = $"d*{date}*{d.Name}";
						await sw.WriteLineAsync(line);
					}

					IEnumerable<string> files = Directory.EnumerateFiles(path);
					foreach (string file in files)
					{
						FileInfo f = new FileInfo(file);
						string date = f.LastWriteTime.ToString("MM dd yyyy");
						string line = $"f*{date}*{f.Name}";
						await sw.WriteLineAsync(line);
					}
					await sw.FlushAsync();
					sw.Close();
					_dataSream.Close();
				}
			}
			return "226 Transfer complete.";
		}
		#endregion
		private async Task HandlePingConnection()
		{
			using (TcpClient tcpClient = await _pingListener.AcceptTcpClientAsync())
			{
				try
				{
					await Task.Run(async () =>
					{
						using (_pingStream = tcpClient.GetStream())
						{
							Stopwatch sw = new Stopwatch();
							while (!_needToStop)
							{
								await Task.Delay(1000);
								var buffer = new byte[1] { 0 };
								sw.Start();
								await _pingStream.WriteAsync(buffer, 0, 1);
								await _pingStream.ReadAsync(buffer, 0, 1);
								LastPing = sw.ElapsedMilliseconds;
								sw.Reset();
								if (buffer[0] == 1) buffer[0] = 2;
								{
									await _pingStream.WriteAsync(buffer, 0, 1);
								}
								//if (verboseDebugging) XDebug.Log($"[Ping] {LastPing}ms", XDebug.DebugColors.Default, logToConsole);
							}
						}
					});
				}
				catch (Exception e)
				{
					OnClientError(e.Message);
					eventsQueue.Enqueue(() =>
					{
						OnClientDisconnected(clientId);
					});
					Cleanup();
				}
			}
		}
		private async Task HandleSpecialConnection()
		{
			using (TcpClient tcpClient = await _specialListener.AcceptTcpClientAsync())
			{
				try
				{
					await Task.Run(async () =>
					{
						using (_specialStream = tcpClient.GetStream())
						using (_specialStreamReader = new StreamReader(_specialStream))
						{
							_specialStreamWriter = new StreamWriter(_specialStream);
							while (!_needToStop)
							{
								string cmd = await _specialStreamReader.ReadLineAsync();
								if (verboseDebugging) XDebug.Log($"[FTPServer] Recieved Special command: {cmd}", XDebug.DebugColors.Default, logToConsole);
								OnCommandFromClient(cmd);
							}
						}
					});
				}
				catch (Exception e)
				{
					_specialStream.Close();
					_specialStream.Dispose();
					OnClientError(e.Message);
					eventsQueue.Enqueue(() =>
					{
						OnClientDisconnected(clientId);
					});
					Cleanup();
				}
			}
		}
		private async Task WriteToClientAsync(string msg)
		{
			while (!_canSendResp && !_needToStop) await Task.Delay(10);
			_canSendResp = false;
			await _clientWriter.WriteLineAsync(msg);
			await _clientWriter.FlushAsync();
			_canSendResp = true;
			if (verboseDebugging) XDebug.Log($"[FTPServer] Response to [{clientId}]: {msg}", XDebug.DebugColors.Green, logToConsole);
		}
		private async Task<string> ExecuteCommandAsync(string msg)
		{
			#region PREPARING COMMAND AND ARGUMENTS
			string cmd = !msg.Contains(' ') ? msg.Replace("\r\n", "") : msg.Substring(0, msg.IndexOf(' ')).ToUpperInvariant().Trim().Replace("\r\n", "");
			string arguments;
			if (msg.Trim().Length == cmd.Length)
			{
				arguments = null;
			}
			else
			{
				arguments = msg.Substring(cmd.Length).Trim();
			}
			if (string.IsNullOrWhiteSpace(arguments)) arguments = null;
			#endregion
			string response = null;
			if (response == null)
			{
				switch (cmd)
				{
					#region FTP spec RFC 959
					case "USER":
						response = UserName(arguments);
						break;
					case "PASS":
						response = Password(arguments);
						break;
					case "ACCT":
						response = Account(arguments);
						break;
					case "CWD":
						response = ChangeWorkingDirecory(arguments);
						break;
					case "CDUP":
						response = ChangeToParentDirectory();
						break;
					case "SMNT":
						response = StructureMount(arguments);
						break;
					case "QUIT":
						response = Logout(arguments);
						break;
					case "REIN":
						response = Reinitialize(arguments);
						break;
					case "PORT":
						response = DataPort(arguments);
						break;
					case "PASV":
						response = Passive(arguments);
						break;
					case "TYPE":
						response = RepresentationType(arguments);
						break;
					case "STRU":
						response = FileStructure(arguments);
						break;
					case "MODE":
						response = TransferMode(arguments);
						break;
					case "RETR":
						response = await Retrieve(arguments);
						break;
					case "STOR":
						response = await Store(arguments);
						break;
					case "STOU":
						response = StoreUnique(arguments);
						break;
					case "APPE":
						response = Append(arguments);
						break;
					case "ALLO":
						response = Allocate(arguments);
						break;
					case "REST":
						response = Restart(arguments);
						break;
					case "RNFR":
						response = RenameFrom(arguments);
						break;
					case "RNTO":
						response = RenameTo(arguments);
						break;
					case "ABOR":
						response = Abort(arguments);
						break;
					case "DELE":
						response = Delete(arguments);
						break;
					case "RMD":
						response = RemoveDirectory(arguments);
						break;
					case "MKD":
						response = MakeDirectory(arguments);
						break;
					case "PWD":
						response = PrintWorkingDirectory(arguments);
						break;
					case "LIST":
						response = await List(arguments);
						break;
					case "NLST":
						response = await NameList(arguments);
						break;
					case "SITE":
						response = SiteParameters(arguments);
						break;
					case "SYST":
						response = System(arguments);
						break;
					case "STAT":
						response = Status(arguments);
						break;
					case "HELP":
						response = Help(arguments);
						break;
					case "NOOP":
						response = Noop(arguments);
						break;
					case "FEAT":
						response = Feat(arguments);
						break;
					case "OPTS":
						response = Opts(arguments);
						break;
					case "SIZE":
						response = Size(arguments);
						break;
					#endregion
					#region MRTFTP spec
					case "RSTP": //restart panel
						response = RestartPanel();
						break;
					case "RSPL": //restart panel launcher
						response = RestartLauncher();
						break;
					case "STP": //stop panel
						response = StopPanel();
						break;
					case "STPL": //stop panel launcher
						response = StopLauncher();
						break;
					case "STTP": //start panel
						response = StartPanel();
						break;
					case "GTSA": //go to streaming assets
						response = GoToStreamingAssets();
						break;
					case "RIN": // remove index in current directory
						response = RemoveIndex();
						break;
					case "RVD": // remove video in current directory
						response = RemoveVideo();
						break;
					case "RIM": // remove image in current directory
						response = RemoveImage();
						break;
					case "RAU": // remove audio in current directory
						response = RemoveAudio();
						break;
					case "RVDS": // remove all videos in current directory
						response = RemoveVideos();
						break;
					case "RIMS": // remove all images in current directory
						response = RemoveImages();
						break;
					case "RAUS": // remove all audios in current directory
						response = RemoveAudios();
						break;
					case "CIN": // create index in current directory
						response = CreateIndex("");
						break;
					case "CLRD":
						response = ClearDirectory(arguments);
						break;
					case "CFR":
						response = CopyFrom(arguments);
						break;
					case "CTO":
						response = await CopyTo(arguments);
						break;
					case "SDBS":
						response = SetDataBufferSize(arguments);
						break;
					case "STPS":
						response = StartPingStream();
						break;
					case "STSS":
						response = StartSpecialStream();
						break;
					case "HPS":
						response = HandlePingStream();
						break;
					case "HSS":
						response = HandleSpecialStream();
						break;
					case "EXLI":
						response = await ExtendedList(arguments);
						break;
					case "GDT":
						response = GetDateTime(arguments);
						break;
					case "GPN":
						response = GetPanelName();
						break;
					#endregion
					default:
						response = "502 Command not implemented";
						break;
				}
			}
			if (!string.IsNullOrEmpty(response) && response.StartsWith("221"))
			{
				eventsQueue.Enqueue(() =>
				{
					OnClientDisconnected(clientId);
				});
			}
			return response;
		}
		#endregion

		#region UTILITIES
		private bool CheckLocalFile(string path, out string fullpath)
		{
			fullpath = Path.Combine(dir.ActualPath, path);
			return CheckFile(fullpath);
		}
		private bool CheckFile(string path)
		{
			return File.Exists(path);
		}
		private bool CheckFolder(string path)
		{
			return Directory.Exists(path);
		}
		private bool CheckLocalFolder(string path, out string fullpath)
		{
			fullpath = Path.Combine(dir.ActualPath, path);
			return CheckFolder(fullpath);
		}
		#endregion

		~AsyncClientConnection()
		{
			Cleanup();
		}
	}
}

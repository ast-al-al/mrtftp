using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MRT.Debug;

namespace MRT.Network.FTP
{
	public class AsyncServerConnection : IDisposable
	{
		#region PROPERTIES
		/// <summary>
		/// Объект рабочей директории подключенного сервера.
		/// </summary>
		public RemoteDir RemoteDir { get; private set; }
		/// <summary>
		/// Объект локальной рабочей директории.
		/// </summary>
		public LocalDir LocalDir { get; private set; }
		public long LastPing { get; private set; }
		#endregion
		#region PUBLIC VARS
		public bool logToConsole = true;
		public bool verboseDebugging = false;
		public ConcurrentQueue<Action> eventsQueue = new ConcurrentQueue<Action>();
		#endregion
		#region PRIVATE VARS
		private int DATA_BUFFER_SIZE = 1024 * 100;
		private readonly string _serverIp;
		private string _remotePath = ".";
		private readonly string _username;
		private readonly string _password;
		private string _result = null;
		private readonly int _port = 21;
		private bool loggedin = false;
		private Socket _clientSocket = null;
		private Socket _pingSocket = null;
		private Socket _specialSocket = null;
		private Task _specialTask;
		private Task _pingTask;
		private NetworkStream _specialStream;
		private StreamReader _specialSR;
		private StreamWriter _specialSW;
		private NetworkStream _clientStream;
		private StreamReader _clientReader;
		private StreamWriter _clientWriter;
		private bool _needToStop = false;
		#endregion
		#region EVENTS
		public event Action<string> OnServerError = (_) => { };
		public event Action OnServerConnected = () => { };
		public event Action OnServerDisconnected = () => { };
		public event Action<string> OnSpecialCommand = (_) => { };
		public event Action<string> OnFileTransitionStart = (_) => { };
		public event Action<string> OnFileTransitionStop = (_) => { };
		#endregion

		#region PUBLIC METHODS
		public AsyncServerConnection(string server, string username, string password, int port)
		{
			_serverIp = server;
			_username = username;
			_password = password;
			_port = port;
			RemoteDir = new RemoteDir(this);
			LocalDir = new LocalDir(this);
		}
		/// <summary>
		/// Отправляет на сервер FTP запрос.
		/// </summary>
		/// <param name="command">Комманда</param>
		/// <returns>Ответ сервера</returns>
		public async Task<string> SendCommandAsync(string command)
		{
			if (verboseDebugging) XDebug.Log($"[FTPClient] Request to server:  {command}", XDebug.DebugColors.Default, logToConsole);
			await _clientWriter.WriteLineAsync(command);
			await _clientWriter.FlushAsync();
			string response = await ReadResponse();
			return response;
		}
		/// <summary>
		/// Отправляет на сервер специальную команду, которая не интерпретируется как FTP команда. Ответ сервера не ожидается.
		/// </summary>
		public async Task SendSpecialCommand(string command)
		{
			if (verboseDebugging) XDebug.Log($"[FTPClient] Sending special command:    {command}", XDebug.DebugColors.Default, logToConsole);
			await _specialSW.WriteLineAsync(command);
			await _specialSW.FlushAsync();
		}
		#region FTP SPEC
		/// <summary>
		/// Авторизация на сервере.
		/// </summary>
		public async Task Login()
		{
			if (loggedin) return;
			if (verboseDebugging) XDebug.Log($"[FtpClient] Opening connection to {_serverIp}", XDebug.DebugColors.Default, logToConsole);
			IPAddress addr;
			IPEndPoint ep;
			bool connected = false;
			while (!connected)
			{
				if (_needToStop) return;
				try
				{
					_clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
					addr = Dns.GetHostEntry(_serverIp).AddressList[0];
					ep = new IPEndPoint(addr, _port);
					_clientSocket.Connect(ep);
					connected = true;
				}
				catch (Exception ex)
				{
					if (_clientSocket != null && _clientSocket.Connected) _clientSocket.Close();
					await Task.Delay(250);
					if (verboseDebugging) XDebug.Log($"[FTPClient] Couldn't connect to remote server: {ex}", XDebug.DebugColors.Default, logToConsole);
					eventsQueue.Enqueue(() =>
					{
						OnServerError(ex.Message);
					});
				}
			}
			_clientStream = new NetworkStream(_clientSocket);
			_clientReader = new StreamReader(_clientStream);
			_clientWriter = new StreamWriter(_clientStream);
			string resp1 = await ReadResponse();
			int respCode1 = GetCode(resp1);
			if (respCode1 != 220)
			{
				await Close();
				eventsQueue.Enqueue(() =>
				{
					OnServerError(resp1.Substring(4));
				});
				return;
			}
			string resp2 = await SendCommandAsync("USER " + _username);
			int respCode2 = GetCode(resp2);
			if (!(respCode2 == 331 || respCode2 == 230))
			{
				Cleanup();
				eventsQueue.Enqueue(() =>
				{
					OnServerDisconnected();
				});
				eventsQueue.Enqueue(() =>
				{
					OnServerError(resp2.Substring(4));
				});
				return;
			}
			if (respCode2 != 230)
			{
				string resp3 = await SendCommandAsync("PASS " + _password);
				int respCode3 = GetCode(resp3);
				if (!(respCode3 == 230 || respCode3 == 202))
				{
					Cleanup();
					eventsQueue.Enqueue(() =>
					{
						OnServerError(resp3.Substring(4));
					});
					eventsQueue.Enqueue(() =>
					{
						OnServerDisconnected();
					});
					return;
				}
			}
			loggedin = true;
			if (verboseDebugging) XDebug.Log($"[FTPClient] Connected to { _serverIp}", XDebug.DebugColors.Default, logToConsole);
			await ChangeDir(this._remotePath);
			await SendCommandAsync("OPTS UTF8 ON");
			await SendCommandAsync("TYPE I");
			_pingTask = Task.Run(HandlePingConnection);
			_specialTask = Task.Run(HandleSpecialConnection);
			eventsQueue.Enqueue(OnServerConnected);
		}
		/// <summary>
		/// Закрывает соединение с сервером и производит очистку.
		/// </summary>
		public async Task Close()
		{
			if (verboseDebugging) XDebug.Log($"[FTPClient] Closing connection to  { _serverIp}", XDebug.DebugColors.Yellow, logToConsole);
			if (_clientSocket != null)
			{
				try
				{
					await SendCommandAsync("QUIT");
				}
				catch { }
			}
			Cleanup();
			eventsQueue.Enqueue(() =>
			{
				OnServerDisconnected();
			});
		}
		/// <summary>
		/// Запрос на получение списка имен файлов и директорий.
		/// </summary>
		/// <param name="justNames">True - запросить только имена. False - запросить список в UNIX формате</param>
		/// <returns>Список имен файлов и директорий</returns>
		public async Task<string[]> List(bool justNames = false)
		{
			if (!loggedin) await Login();
			Socket cSocket = await СreateDataSocket();
			string response = await SendCommandAsync(justNames ? "NLST" : "LIST");
			string message = await RecieveStringAsync(cSocket);
			string[] msg = message.Replace("\r", "").Split('\n');
			string res = await ReadResponse();
			cSocket.Close();
			if (message.IndexOf("No such file or directory") != -1)
			{
				msg = new string[] { };
				eventsQueue.Enqueue(() =>
				{
					OnServerError(message);
				});
			}
			System.Collections.Generic.List<string> temp = new System.Collections.Generic.List<string>(msg);
			int count = temp.Count;
			for (int i = count - 1; i >= 0; i--)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] {temp[i]}", XDebug.DebugColors.Default, logToConsole);
				if (string.IsNullOrWhiteSpace(temp[i])) temp.RemoveAt(i);
			}
			return temp.ToArray();
		}
		/// <summary>
		/// Запрос на получение списка имен директорий в текущей директории.
		/// <remarks>
		/// <br>Пример:</br>
		/// <br>"d*mm dd yyyy*dir_name" - для директории</br>
		/// <br>"f*mm dd yyyy*file_name.extension" - для файла</br>
		/// </remarks>
		/// </summary>
		/// <returns>Список имен директорий в текущей директории</returns>
		public async Task<string[]> ExtendedList()
		{
			if (!loggedin) await Login();
			Socket cSocket = await СreateDataSocket();
			string response = await SendCommandAsync("EXLI");
			string message = await RecieveStringAsync(cSocket);
			string[] msg = message.Replace("\r", "").Split('\n');
			string res = await ReadResponse();
			cSocket.Close();
			if (message.IndexOf("No such file or directory") != -1)
			{
				msg = new string[] { };
				eventsQueue.Enqueue(() =>
				{
					OnServerError(message);
				});
			}
			System.Collections.Generic.List<string> temp = new System.Collections.Generic.List<string>(msg);
			int count = temp.Count;
			for (int i = count - 1; i >= 0; i--)
			{
				if (verboseDebugging) XDebug.Log(temp[i], XDebug.DebugColors.Default, logToConsole);
				if (string.IsNullOrWhiteSpace(temp[i])) temp.RemoveAt(i);
			}
			return temp.ToArray();
		}
		/// <summary>
		/// Запрос на получение размера файла в байтах.
		/// </summary>
		/// <param name="fileName">Относительный путь к файлу</param>
		/// <returns>Размер файла</returns>
		public async Task<long> GetFileSize(string fileName)
		{
			if (!loggedin) await Login();
			string response = await SendCommandAsync("SIZE " + fileName);
			int respCode = GetCode(response);
			long size = 0;
			if (respCode == 213)
			{
				size = long.Parse(response.Substring(4));
			}
			else
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(response);
				});
			}
			return size;
		}
		/// <summary>
		/// Запрос на скачивание файла с сервера в текущую локальную директорию.
		/// </summary>
		/// <param name="remFileName">Оносительный путь к файлу</param>
		public async Task Download(string remFileName)
		{
			await Download(remFileName, "");
		}
		/// <summary>
		/// Запрос на скачивание фала с сервера.
		/// </summary>
		/// <param name="remFileName">Относительный путь к фалу на сервере</param>
		/// <param name="locFileName">Относительный путь для сохранения на клиент. Оставить пустым, чтобы не менять имя и скачать в текущую директорию</param>
		public async Task Download(string remFileName, string locFileName)
		{
			if (!loggedin) await Login();
			await SendCommandAsync("TYPE I");
			if (verboseDebugging) XDebug.Log("[FTPClient]Downloading file " + remFileName + " from " + _serverIp + "/" + _remotePath, XDebug.DebugColors.Default, logToConsole);
			if (locFileName.Equals(""))
			{
				locFileName = remFileName;
			}
			locFileName = Path.Combine(LocalDir.ActualPath, locFileName);
			Socket cSocket = await СreateDataSocket();
			string response2 = await SendCommandAsync("RETR " + remFileName);
			int respCode2 = GetCode(response2);
			if (respCode2 != 150 && respCode2 != 125)
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(response2);
				});
				return;
			}
			eventsQueue.Enqueue(() => { OnFileTransitionStart(locFileName); });
			await NetworkUtils.WriteFileFromSocketAsync(locFileName, cSocket, DATA_BUFFER_SIZE);
			if (cSocket.Connected) cSocket.Close();
			eventsQueue.Enqueue(() => { OnFileTransitionStop(locFileName); });
			string resp3 = await ReadResponse();
			int respCode3 = GetCode(resp3);
			if (respCode2 != 226 && respCode2 != 250)
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(response2);
				});
			}
		}
		/// <summary>
		/// Запрос на отправку файла на сервер в текущую удаленную директорию.
		/// </summary>
		/// <param name="fileName">Полный путь к файлу</param>
		public async Task Upload(string fileName)
		{
			await Upload(fileName, false);
		}
		/// <summary>
		/// Запрос на отправку файла на сервер в текущую удаленную директорию.
		/// </summary>
		/// <param name="fileName">Полный путь к файлу</param>
		/// <param name="resume">Продолжить незавершенную передачу?</param>
		public async Task Upload(string fileName, bool resume)
		{
			if (!loggedin) await Login();
			Socket cSocket;
			long offset = 0;
			if (resume)
			{
				try
				{
					await SendCommandAsync("TYPE I");
					offset = await GetFileSize(Path.GetFileName(fileName));
				}
				catch (Exception)
				{
					// file not exist
					offset = 0;
				}
			}
			cSocket = await СreateDataSocket();
			if (offset > 0)
			{
				string resp1 = await SendCommandAsync("REST " + offset);
				int respCode1 = GetCode(resp1);
				if (respCode1 != 350)
				{
					if (verboseDebugging) XDebug.Log("[FTPClient] Resuming not supported", XDebug.DebugColors.Yellow, logToConsole);
					offset = 0;
				}
			}
			string response = await SendCommandAsync("STOR " + Path.GetFileName(fileName));
			int respCode = GetCode(response);
			if (respCode != 125 && respCode != 150)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] {_result}", XDebug.DebugColors.Red, logToConsole);
				eventsQueue.Enqueue(() =>
				{
					OnServerError(_result);
				});
			}
			if (offset != 0)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] Resuming at offset {offset}", XDebug.DebugColors.Default, logToConsole);
				//input.Seek(offset, SeekOrigin.Begin);
			}
			if (verboseDebugging) XDebug.Log($"[FTPClient] Uploading file {fileName}", XDebug.DebugColors.Default, logToConsole);
			eventsQueue.Enqueue(() => { OnFileTransitionStart(fileName); });
			await NetworkUtils.SendFileToSocketAsync(fileName, cSocket, DATA_BUFFER_SIZE);
			eventsQueue.Enqueue(() => { OnFileTransitionStop(fileName); });
			cSocket.Shutdown(SocketShutdown.Both);
			cSocket.Close();
			cSocket.Dispose();
			string response2 = await ReadResponse();
			int respCode2 = GetCode(response2);
			if (respCode2 != 226 && respCode2 != 250)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] {_result}", XDebug.DebugColors.Red, logToConsole);
				eventsQueue.Enqueue(() =>
				{
					OnServerError(_result);
				});
			}
		}
		/// <summary>
		/// Запрос на загрузку локальной директории на сервер в текущую удаленную директорию.
		/// </summary>
		/// <param name="path">Полный путь к директории</param>
		/// <param name="recurse">Рекурсивно загружать подпапки?</param>
		public async Task UploadDirectory(string path, bool recurse)
		{
			await UploadDirectory(path, recurse, "*.*");
		}
		/// <summary>
		/// Запрос на загрузку локальной директории на сервер в текущую удаленную директорию.
		/// </summary>
		/// <param name="path">Полный путь к директории</param>
		/// <param name="recurse">Рекурсивно загружать подпапки?</param>
		/// <param name="mask">Маска имени файлов для загрузки. Все файлы - "*.*"</param>
		public async Task UploadDirectory(string path, bool recurse, string mask) //+
		{
			if (!loggedin) await Login();
			string[] dirs = path.Replace("/", @"\").Split('\\');
			string rootDir = dirs[dirs.Length - 1];

			string[] d = await List(true);
			bool hasFolder = false;
			foreach (var item in d)
			{
				if (item == rootDir)
				{
					hasFolder = true;
					break;
				}
			}
			if (!hasFolder) await MakeDir(rootDir);
			await ChangeDir(rootDir);
			foreach (string file in Directory.EnumerateFiles(path, mask))
			{
				await Upload(file, true);
			}
			if (recurse)
			{
				foreach (string directory in Directory.GetDirectories(path))
				{
					await UploadDirectory(directory, recurse, mask);
				}
			}
			await ChangeDir("..");
		}
		/// <summary>
		/// Запрос на удаление файла на сервере.
		/// </summary>
		/// <param name="fileName">Относительный путь к файлу</param>
		public async Task DeleteFile(string fileName)
		{
			if (!loggedin) { await Login(); }
			string resp1 = await SendCommandAsync("DELE " + fileName);
			int respCode1 = GetCode(resp1);
			if (respCode1 != 250)
			{
				eventsQueue.Enqueue(() => { OnServerError(resp1.Substring(4)); });
			}
			else if (verboseDebugging) XDebug.Log($"[FTPClient] Deleted file {fileName}", XDebug.DebugColors.Default, logToConsole);
		}
		/// <summary>
		/// Запрос на переименование файла на сервере.
		/// </summary>
		/// <param name="oldFileName">Относительный путь к файлу для переименования</param>
		/// <param name="newFileName">Новый относительный путь к файлу</param>
		public async Task RenameFile(string oldFileName, string newFileName)
		{
			if (!loggedin) await Login();
			await SendCommandAsync("RNFR " + oldFileName);
			string resp1 = await SendCommandAsync("RNTO " + newFileName);
			int respCode1 = GetCode(resp1);
			if (respCode1 != 250)
			{
				eventsQueue.Enqueue(() => { OnServerError(resp1.Substring(4)); });
			}
		}
		/// <summary>
		/// Create a directory on the remote FTP server.
		/// </summary>
		/// <param name="dirName"></param>
		public async Task MakeDir(string dirName)
		{
			if (!loggedin) await Login();
			string resp1 = await SendCommandAsync("MKD " + dirName);
			int respCode1 = GetCode(resp1);
			if (respCode1 != 250 && respCode1 != 257)
			{
				eventsQueue.Enqueue(() => { OnServerError(resp1.Substring(4)); });
			}
		}
		/// <summary>
		/// Delete a directory on the remote FTP server.
		/// </summary>
		/// <param name="dirName"></param>
		public async Task RemoveDir(string dirName)
		{
			if (!loggedin) await Login();
			string resp1 = await SendCommandAsync("RMD " + dirName);
			int respCode1 = GetCode(resp1);
			if (respCode1 != 250)
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(resp1.Substring(4));
				});
			}
		}
		/// <summary>
		/// Change the current working directory on the remote FTP server.
		/// </summary>
		/// <param name="dirName"></param>
		public async Task ChangeDir(string dirName)
		{
			if (dirName == null || dirName.Equals(".") || dirName.Length == 0)
			{
				return;
			}
			if (!loggedin) await Login();
			string resp1 = await SendCommandAsync("CWD " + dirName);
			int respCode1 = GetCode(resp1);
			if (respCode1 != 250)
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(_result.Substring(4));
				});
			}
			string resp2 = await SendCommandAsync("PWD");
			int respCode2 = GetCode(resp2);
			if (respCode2 != 257)
			{
				eventsQueue.Enqueue(() =>
				{
					OnServerError(_result.Substring(4));
				});
			}
			_remotePath = resp2.Split('"')[1];
		}
		/// <summary>
		/// Получает путь к текущей директории на сервере.
		/// </summary>
		/// <returns>Путь к директории</returns>
		public async Task<string> PrintWorkingDirectory()
		{
			string resp = await SendCommandAsync("PWD");
			int firstIndex = resp.IndexOf('"') + 1;
			int lastIndex = resp.LastIndexOf('"');
			int length = lastIndex - firstIndex;
			resp = resp.Substring(firstIndex, length);
			return resp;
		}
		/// <summary>
		/// Рекурсивно скачивает содержимое удаленной директории в текущую локальную директорию.
		/// </summary>
		/// <param name="name">Имя директории для скачивания (оставить пустым, чтобы скачать текущую удаленную директорию)</param>
		/// <param createLocalDir="createLocalDir">Если true - создает локальную директорию с указанным именем 
		/// или с именем текущей удаленной директории</param>
		/// <returns></returns>
		public async Task DownloadDirectory(string name, bool createLocalDir)
		{
			await ChangeDir(name);
			if (createLocalDir) LocalDir.CreateDirectory(name, true);
			string[] fd = await ExtendedList();
			foreach (var item in fd)
			{
				bool isFile = item[0] == 'f';
				if (isFile)
				{
					await Download(item.Substring(item.LastIndexOf('*') + 1));
				}
				else
				{
					await DownloadDirectory(item.Substring(item.LastIndexOf('*') + 1), true);
				}
			}
			LocalDir.UpDirectory();
			await RemoteDir.UpDirectory();
		}
		#endregion
		#region MRTFTP SPEC
		/// <summary>
		/// Запрос на переход сервера в директорию StreamingAssets.
		/// </summary>
		/// <returns>Полный путь к директории StreamingAssets</returns>
		public async Task<string> GoToStreamingAssets()
		{
			await SendCommandAsync("GTSA");
			return await PrintWorkingDirectory();
		}
		/// <summary>
		/// Запрос на удаление директории на сервере.
		/// </summary>
		/// <param name="path">Относительный путь к директории. Если путь пустой - очищает текущую директорию</param>
		public async Task ClearDirectory(string path)
		{
			await SendCommandAsync($"CLRD {path}");
		}
		/// <summary>
		/// Запрос на установку размера буфера для передачи файлов и массивов данных.
		/// </summary>
		/// <param name="size">Размер в байтах</param>
		public async Task SetDataBufferSize(int size)
		{
			DATA_BUFFER_SIZE = size;
			await SendCommandAsync($"SDBS {size}");
		}
		/// <summary>
		/// Синхронизирует удаленную директорию с локальной.
		/// </summary>
		/// <param name="path">Относительный путь к директории. 
		/// Оставить пустым для синхронизации текущей локальной директории</param>
		/// <param name="syncSubdirectories">Синхронизировать дочерние директории?</param>
		public async Task SyncFolder(string path, bool syncSubdirectories = true)
		{
			string oldRemDir = await PrintWorkingDirectory();
			string oldLocDir = LocalDir.ActualPath;

			if (!string.IsNullOrEmpty(path)) LocalDir.DownDirectory(path);

			// путь относительно StreamingAssets
			string fullRelativePath = LocalDir.StreamingAssetsPath.Replace(LocalDir.ActualPath, "");
			//синхронизация локального и удаленного относитльных путей
			await GoToStreamingAssets();
			if (!string.IsNullOrEmpty(fullRelativePath) && !string.IsNullOrWhiteSpace(fullRelativePath))
			{
				await MakeDir(fullRelativePath);
				await ChangeDir(fullRelativePath);
			}

			await SimpleSyncDir("", syncSubdirectories);

			//возвращаемся в исходные директории
			LocalDir.GoToPath(oldLocDir);
			await ChangeDir(oldRemDir);
		}
		private async Task SimpleSyncDir(string relativePath, bool syncSubdirectories = true)
		{
			// если путь не пустой - заходим в папку и помечаем что нужно вернуться
			bool needToGoUp = false;
			if (!string.IsNullOrEmpty(relativePath))
			{
				LocalDir.DownDirectory(relativePath);
				await ChangeDir(relativePath);
				needToGoUp = true;
			}

			//удаляем на сервере файлы и папки, которых нет на клиенте
			DirectoryInfo currDir = new DirectoryInfo(LocalDir.ActualPath);
			string[] remList = await ExtendedList();
			foreach (var item in remList)
			{
				if (item[0] == 'd') // папка
				{
					if (!Directory.Exists(Path.Combine(LocalDir.ActualPath, item.Substring(item.LastIndexOf('*') + 1))))
					{
						await RemoveDir(item.Substring(item.LastIndexOf('*') + 1));
					}
				}
				else // файл
				{
					if (!File.Exists(Path.Combine(LocalDir.ActualPath, item.Substring(item.LastIndexOf('*') + 1))))
					{
						await DeleteFile(item.Substring(item.LastIndexOf('*') + 1));
					}
				}
			}

			// загружаем новые файлы и папки (заменяем, если существующие различаются по имени или размеру. Скачиваем если на панели более новые)
			foreach (var item in currDir.EnumerateFiles())
			{
				DateTime remDateTime = await GetDateTime(item.Name);
				if (remDateTime == DateTime.MinValue) // файл не существует
				{
					await Upload(item.FullName);
				}
				else
				{
					if (remDateTime == item.LastWriteTime)
					{
						long remFileSize = await GetFileSize(item.Name);
						if (remFileSize != item.Length)
						{
							await DeleteFile(item.Name);
							await Upload(item.FullName);
						}
					}
					else if (remDateTime < item.LastWriteTime)
					{
						await DeleteFile(item.Name);
						await Upload(item.FullName);
					}
					else
					{
						string fileToDownload = item.Name;
						item.Delete();
						await Download(fileToDownload);
					}
					//XDebug.Log($"remDateTime:{remDateTime}   LastWriteTime:{item.LastWriteTime}   remFileSize:{remFileSize}   Length:{item.Length}", XDebug.DebugColors.Red, logToConsole);
				}
			}
			if (syncSubdirectories)
			{
				foreach (var item in currDir.EnumerateDirectories())
				{
					DateTime remDate = await GetDateTime(item.Name);
					if (remDate == DateTime.MinValue) // папка не существует
					{
						await UploadDirectory(item.FullName, true);
					}
					else
					{
						await SimpleSyncDir(item.Name);
					}
				}
			}
			if (needToGoUp)
			{
				await RemoteDir.UpDirectory();
				LocalDir.UpDirectory();
			}
		}
		/// <summary>
		/// <br>Синхронизирует файл на сервере с локальным файлом.</br>
		/// <br>Для синхронизации всех файлов директории используйте SyncFolder("путь", true).</br>
		/// </summary>
		/// <param name="name">Имя файла в текущей локальной директории</param>
		public async Task SyncFile(string name)
		{
			if (string.IsNullOrEmpty(name)) return;
			string oldRemDir = await PrintWorkingDirectory();
			string fullRelativePath = LocalDir.StreamingAssetsPath.Replace(LocalDir.ActualPath, "");
			await GoToStreamingAssets();
			await ChangeDir(fullRelativePath);
			FileInfo fileInfo = new FileInfo(Path.Combine(LocalDir.ActualPath, name));
			DateTime remDate = await GetDateTime(name);
			if (remDate == DateTime.MinValue)
			{
				await Upload(fileInfo.FullName);
			}
			else
			{
				long remFileSize = await GetFileSize(name);
				if (remDate != fileInfo.LastWriteTime || remFileSize != fileInfo.Length)
				{
					await DeleteFile(name);
				}
			}
			await ChangeDir(oldRemDir);
		}
		/// <summary>
		/// Запрос на получение даты последнего редактирования файла/папки.
		/// </summary>
		/// <param name="path">Относительный или полный путь к файлу/папке</param>
		/// <returns>Дата последнего редактирования</returns>
		public async Task<DateTime> GetDateTime(string path)
		{
			string resp = await SendCommandAsync("GDT " + path);
			if (resp[0] == '5') return DateTime.MinValue;
			string[] parts = resp.Split(' ');
			DateTime result = new DateTime(int.Parse(parts[3]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[4]), int.Parse(parts[5]), int.Parse(parts[6]));
			return result;
		}
		/// <summary>
		/// Запрашивает имя подключенной панели. (имя exe файла без расширения)
		/// </summary>
		/// <returns>Имя панели. Null, если файлы панели не найдены</returns>
		public async Task<string> GetPanelName()
		{
			string resp = await SendCommandAsync("GPN");
			if (resp[0] == '5') return null;
			return resp.Substring(4);
		}
		#endregion
		/// <summary>
		/// Очищает все ресурсы и останавливает потоки.
		/// </summary>
		public void Cleanup()
		{
			_needToStop = true;
			if (_clientReader != null)
			{
				_clientReader.Close();
				_clientReader.Dispose();
			}
			if (_clientWriter != null)
			{
				_clientWriter.Close();
				_clientWriter.Dispose();
			}
			if (_clientStream != null)
			{
				_clientStream.Close();
				_clientStream.Dispose();
			}
			if (_clientSocket != null)
			{
				_clientSocket.Close();
				_clientSocket = null;
			}
			if (_specialSocket != null)
			{
				_specialSocket.Close();
				_specialSocket.Dispose();
			}
			if (_specialSW != null)
			{
				_specialSW.Close();
				_specialSW.Dispose();
			}
			if (_specialSR != null)
			{
				_specialSR.Close();
				_specialSR.Dispose();
			}
			if (_pingSocket != null)
			{
				_pingSocket.Close();
				_pingSocket.Dispose();
			}
			loggedin = false;
		}
		public void Dispose()
		{
			Cleanup();
		}
		#endregion
		#region PRIVATE METHODS
		private async Task<string> ReadResponse()
		{
			_result = await _clientReader.ReadLineAsync();
			if (verboseDebugging) XDebug.Log($"[FTPClient] Response from server: {_result}", XDebug.DebugColors.Green, logToConsole);
			return _result;
		}
		private int GetCode(string msg)
		{
			int result = 0;
			if (!string.IsNullOrEmpty(msg) && char.IsDigit(msg[0]))
			{
				if (msg.Length > 3)
				{
					result = int.Parse(msg.Substring(0, msg.IndexOf(' ')));
				}
				else
				{
					result = int.Parse(msg.Trim());
				}
			}
			return result;
		}
		private async Task<string> RecieveStringAsync(Socket socket)
		{
			using (NetworkStream ns = new NetworkStream(socket))
			using (StreamReader sr = new StreamReader(ns))
			{
				return await sr.ReadToEndAsync();
			}
		}
		private async Task<Socket> СreateDataSocket()
		{
			string resp1 = await SendCommandAsync("PASV");
			int respCode1 = GetCode(resp1);
			if (respCode1 != 227)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] {resp1}", XDebug.DebugColors.Red, logToConsole);
				eventsQueue.Enqueue(() =>
				{
					OnServerError(_result);
				});
				return null;
			}
			int index1 = _result.IndexOf('(');
			int index2 = _result.IndexOf(')');
			string ipData = _result.Substring(index1 + 1, index2 - index1 - 1);
			int[] parts = new int[6];
			int len = ipData.Length;
			int partCount = 0;
			string buf = "";
			for (int i = 0; i < len && partCount <= 6; i++)
			{
				char ch = char.Parse(ipData.Substring(i, 1));

				if (char.IsDigit(ch))
					buf += ch;

				else if (ch != ',')
				{
					if (verboseDebugging) XDebug.Log($"[FTPClient] {_result}", XDebug.DebugColors.Red, logToConsole);
					eventsQueue.Enqueue(() =>
					{
						OnServerError(_result);
					});
					return null;
				}

				if (ch == ',' || i + 1 == len)
				{
					try
					{
						parts[partCount++] = int.Parse(buf);
						buf = "";
					}
					catch (Exception ex)
					{
						if (verboseDebugging) XDebug.Log($"[FTPClient] {ex.Message}", XDebug.DebugColors.Red, logToConsole);
						eventsQueue.Enqueue(() =>
						{
							OnServerError(ex.Message);
						});
						return null;
					}
				}
			}
			string ipAddress = parts[0] + "." + parts[1] + "." + parts[2] + "." + parts[3];
			int port = 256 * parts[4] + parts[5];
			Socket socket = null;
			IPEndPoint ep;
			try
			{
				socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				IPAddress addr = Dns.GetHostEntry(ipAddress).AddressList[0];
				ep = new IPEndPoint(addr, port);
				socket.Connect(ep);
			}
			catch (Exception ex)
			{
				if (socket != null && socket.Connected) socket.Close();
				if (verboseDebugging) XDebug.Log($"[FTPClient] {ex.Message}", XDebug.DebugColors.Red, logToConsole);
			}
			return socket;
		}
		private async Task HandlePingConnection()
		{
			string resp1 = await SendCommandAsync("STPS");
			int index1 = resp1.IndexOf('(');
			int index2 = resp1.IndexOf(')');
			string ipData = resp1.Substring(index1 + 1, index2 - index1 - 1);
			int[] parts = new int[6];
			int len = ipData.Length;
			int partCount = 0;
			string buf = "";
			for (int i = 0; i < len && partCount <= 6; i++)
			{
				char ch = char.Parse(ipData.Substring(i, 1));

				if (char.IsDigit(ch))
					buf += ch;

				else if (ch != ',')
				{
					if (verboseDebugging) XDebug.Log($"[FTPClient] {_result}", XDebug.DebugColors.Red, logToConsole);
					eventsQueue.Enqueue(() =>
					{
						OnServerError(_result);
					});
					return;
				}

				if (ch == ',' || i + 1 == len)
				{
					try
					{
						parts[partCount++] = int.Parse(buf);
						buf = "";
					}
					catch (Exception ex)
					{
						if (verboseDebugging) XDebug.Log($"[FTPClient] {ex.Message}", XDebug.DebugColors.Red, logToConsole);
						eventsQueue.Enqueue(() =>
						{
							OnServerError(ex.Message);
						});
						return;
					}
				}
			}
			string ipAddress = parts[0] + "." + parts[1] + "." + parts[2] + "." + parts[3];
			int port = 256 * parts[4] + parts[5];
			if (verboseDebugging) XDebug.Log($"Ping connection ip: {ipAddress}  port:{port}", XDebug.DebugColors.Green, logToConsole);
			IPEndPoint ep;
			try
			{
				_pingSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				IPAddress addr = Dns.GetHostEntry(ipAddress).AddressList[0];
				ep = new IPEndPoint(addr, port);
				_pingSocket.Connect(ep);
			}
			catch (Exception ex)
			{
				if (verboseDebugging) XDebug.Log($"[FTPClient] {ex.Message}", XDebug.DebugColors.Red, logToConsole);
				eventsQueue.Enqueue(() =>
				{
					OnServerError(ex.Message);
				});
			}
			await SendCommandAsync("HPS");
			try
			{
				await Task.Run(async () =>
				{
					Stopwatch sw = new Stopwatch();
					while (!_needToStop)
					{
						byte[] buffer = new byte[1];
						int result1 = await NetworkUtils.RecievePacketAsync(_pingSocket, buffer);
						buffer[0] = 1;
						sw.Start();
						await NetworkUtils.SendPacketAsync(_pingSocket, buffer, 0, 1, SocketFlags.None);
						int result2 = await NetworkUtils.RecievePacketAsync(_pingSocket, buffer);
						LastPing = sw.ElapsedMilliseconds;
						sw.Reset();
						//if (_verboseDebugging) XDebug.Log($"[Ping] {sw.ElapsedMilliseconds}ms", XDebug.DebugColors.Default, logToConsole);
					}
				});
			}
			catch
			{
				Cleanup();
				eventsQueue.Enqueue(() =>
				{
					OnServerDisconnected();
				});
			}
		}
		private async Task HandleSpecialConnection()
		{
			string resp1 = await SendCommandAsync("STSS");
			int index1 = resp1.IndexOf('(');
			int index2 = resp1.IndexOf(')');
			string ipData = resp1.Substring(index1 + 1, index2 - index1 - 1);
			int[] parts = new int[6];
			int len = ipData.Length;
			int partCount = 0;
			string buf = "";
			for (int i = 0; i < len && partCount <= 6; i++)
			{
				char ch = char.Parse(ipData.Substring(i, 1));

				if (char.IsDigit(ch))
					buf += ch;

				else if (ch != ',')
				{
					if (verboseDebugging) XDebug.Log($"[FTPClient] {_result}", XDebug.DebugColors.Red, logToConsole);
					OnServerError(_result);
					return;
				}

				if (ch == ',' || i + 1 == len)
				{
					try
					{
						parts[partCount++] = int.Parse(buf);
						buf = "";
					}
					catch (Exception ex)
					{
						if (verboseDebugging) XDebug.Log($"[FTPClient] {ex.Message}", XDebug.DebugColors.Red, logToConsole);
						OnServerError(ex.Message);
						return;
					}
				}
			}
			string ipAddress = parts[0] + "." + parts[1] + "." + parts[2] + "." + parts[3];
			int port = 256 * parts[4] + parts[5];
			if (verboseDebugging) XDebug.Log($"Special connection ip: {ipAddress}  port:{port}", XDebug.DebugColors.Green, logToConsole);
			IPEndPoint ep;
			try
			{
				_specialSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				IPAddress addr = Dns.GetHostEntry(ipAddress).AddressList[0];
				ep = new IPEndPoint(addr, port);
				_specialSocket.Connect(ep);
				_specialStream = new NetworkStream(_specialSocket);
				_specialSW = new StreamWriter(_specialStream);
				_specialSR = new StreamReader(_specialStream);
			}
			catch (Exception ex)
			{
				if (_specialSocket != null && _specialSocket.Connected) _specialSocket.Close();
				if (_specialStream != null)
				{
					_specialSW.Close();
					_specialSW.Dispose();
					_specialSR.Close();
					_specialSR.Dispose();
					_specialStream.Close();
					_specialStream.Dispose();
				}
				if (verboseDebugging) XDebug.Log($"[FTPClient] SpecialStream: {ex.Message}", XDebug.DebugColors.Red, logToConsole);
				eventsQueue.Enqueue(() =>
				{
					OnServerError(ex.Message);
				});
				return;
			}
			await SendCommandAsync("HSS");
			await Task.Delay(1000);
			try
			{
				await Task.Run(async () =>
				{
					while (!_needToStop)
					{
						string cmd = await _specialSR.ReadLineAsync();
						eventsQueue.Enqueue(() =>
						{
							OnSpecialCommand(cmd);
						});
						if (verboseDebugging) XDebug.Log($"[FTPClient] Special command: {cmd}", XDebug.DebugColors.Default, logToConsole);
					}
				});
			}
			catch
			{
				Cleanup();
			}
		}
		#endregion
		
		~AsyncServerConnection()
		{
			Cleanup();
		}
	}
}

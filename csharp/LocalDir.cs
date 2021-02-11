using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace MRT.Network.FTP
{
	/// <summary>
	/// Набор функций для работы с локальной директорией.
	/// </summary>
	public class LocalDir
	{
		#region PROPERTIES
		/// <summary>
		/// Имя Unity проекта. Если фалы проекта не найдены - "DIR_NOT_FOUND". Значение кешируется до перезапуска программы!
		/// </summary>
		public string StringNameOfActivePanel
		{
#if UNITY_EDITOR || UNITY_STANDALONE
            get
            {
				if (!string.IsNullOrEmpty(_stringNameOfActivePanel)) return _stringNameOfActivePanel;
                DirectoryInfo data = new DirectoryInfo(Application.DataPath);
				return _stringNameOfActivePanel = data.Name.Replace("_Data", "");
            }
#else
			get
			{
				if (_stringNameOfActivePanel != null) return _stringNameOfActivePanel;
				string[] directories = Directory.EnumerateDirectories(System.AppDomain.CurrentDomain.BaseDirectory).ToArray();
				foreach (var item in directories)
				{
					if (item.Contains("_Data"))
					{
						_stringNameOfActivePanel = Path.GetFileNameWithoutExtension(item).Replace("_Data", "");
						return _stringNameOfActivePanel;
					}
				}
				return "DIR_NOT_FOUND";
			}
#endif
		}
		/// <summary>
		/// Путь к папке StreamingAssets. Если не найдено - "DIR_NOT_FOUND". Значение кешируется до перезапуска программы!
		/// </summary>
		public string StreamingAssetsPath
		{
#if UNITY_EDITOR || UNITY_STANDALONE
            get
            {
				if (!string.IsNullOrEmpty(_GetStreamingAssetsPath)) return _GetStreamingAssetsPath;
                return _streamingAssetsPath = Path.Combine(Application.dataPath, "StreamingAssets");
            }
#else
			get
			{
				if (!string.IsNullOrEmpty(_streamingAssetsPath)) return _streamingAssetsPath;
				if (StringNameOfActivePanel == "DIR_NOT_FOUND") return "DIR_NOT_FOUND";
				return _streamingAssetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, StringNameOfActivePanel + "_Data", "StreamingAssets");
			}
#endif
			private set
			{
				_streamingAssetsPath = value;
			}
		}
		/// <summary>
		/// Текущая директория.
		/// </summary>
		public string ActualPath
		{
			get
			{
				CheckDirForInitialized();
				return _actualPath;
			}
			set
			{
				_actualPath = value;
			}
		}
		/// <summary>
		/// Путь к копии папки StreamingAssets.
		/// </summary>
		public string StreamingAssetsCopyPath
		{
			get { return Path.Combine($"{Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StreamingAssetsCopies")}", StringNameOfActivePanel); }
		}
		#endregion
		#region PRIVATE VARS
		private AsyncServerConnection serverConnection;
		private string _stringNameOfActivePanel = null; // caсhe
		private string _streamingAssetsPath = null; // caсhe
		private string _actualPath;
		#endregion
		public LocalDir(AsyncServerConnection serverConnection)
		{
			this.serverConnection = serverConnection;
		}
		public LocalDir()
		{
			
		}

		#region PUBLIC METHODS
		/// <summary>
		/// Очищает папку StreamingAssets.
		/// </summary>
		public void ClearStreamingAssets()
		{
			if (Directory.Exists(StreamingAssetsPath))
			{
				try
				{
					Directory.Delete(StreamingAssetsPath, true);
				}
				catch (Exception e)
				{
					System.Diagnostics.Debug.WriteLine("failed clear StreamingAssets: " + e);
					Thread.Sleep(1000);
					ClearStreamingAssets();

				}
			}
			Directory.CreateDirectory(StreamingAssetsPath);
		}
		/// <summary>
		/// Копирует содержимое StreamingAssets в StreamingAssetsCopies.
		/// </summary>
		public void CopyStreamingAssets()
		{
			if (Directory.Exists(StreamingAssetsPath))
			{
				if (Directory.Exists(StreamingAssetsCopyPath))
				{
					try
					{
						Directory.Delete(StreamingAssetsCopyPath, true);
					}
					catch (Exception e)
					{
						System.Diagnostics.Debug.WriteLine("failed clear StreamingAssets: " + e);
						Thread.Sleep(1000);
						ClearStreamingAssets();

					}
				}
				Directory.CreateDirectory(StreamingAssetsCopyPath);
				CopyDirectory(StreamingAssetsPath, StreamingAssetsCopyPath);
			}
		}
		/// <summary>
		/// Копирует содержимое StreamingAssetsCopies в StreamingAssets.
		/// </summary>
		/// <param name="callback">Вызывается по окончанию копирования</param>
		public void CopyStreamingAssetsBack(Action callback = null)
		{
			if (Directory.Exists(StreamingAssetsPath))
			{
				if (Directory.Exists(StreamingAssetsCopyPath))
				{
					ClearStreamingAssets();
					CopyDirectory(StreamingAssetsCopyPath, StreamingAssetsPath, callback);
				}
			}
		}
		/// <summary>
		/// Копирует директорию и ее содержимое в новую локацию.
		/// </summary>
		/// <param name="from">Полный путь к директории для копирования</param>
		/// <param name="to">Полный путь к новой директории</param>
		/// <param name="callback">Вызывается после завершения копирования</param>
		public void CopyDirectory(string from, string to, Action callback = null)
		{
			if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
			if (!Directory.Exists(from)) return;
			if (!Directory.Exists(to)) Directory.CreateDirectory(to);
			DirectoryInfo fromDir = new DirectoryInfo(from);
			foreach (var item in fromDir.EnumerateFiles())
			{
				item.CopyTo(Path.Combine(to, item.Name));
			}
			foreach (var item in fromDir.EnumerateDirectories())
			{
				CopyDirectory(item.FullName, Path.Combine(to, item.Name));
			}
			if (callback != null) callback.Invoke();
		}
		/// <summary>
		/// Удаляет содержимое директории.
		/// </summary>
		/// <param name="path">Полный путь к директории</param>
		public void ClearDirectory(string path)
		{
			if (Directory.Exists(path))
			{
				try
				{
					Directory.Delete(path, true);
				}
				catch (Exception e)
				{
					System.Diagnostics.Debug.WriteLine("failed clear dir \"" + path + "\" : " + e);
					Thread.Sleep(1000);
					ClearStreamingAssets();

				}
			}
			Directory.CreateDirectory(path);
		}
		/// <summary>
		/// Преходит в указанную директорию.
		/// </summary>
		/// <param name="path">Полный путь к директории</param>
		public void GoToPath(string path)
		{
			ActualPath = path;
		}
		/// <summary>
		/// Переходит в директорию StreamingAssets.
		/// </summary>
		public void GoToStreamingAssets()
		{
			ActualPath = StreamingAssetsPath;
		}
		/// <summary>
		/// Создает подкаталог.
		/// </summary>
		/// <param name="name">Имя</param>
		/// <param name="createThenOpen">Перейти в подкаталог после создания?</param>
		public void CreateDirectory(string name, bool createThenOpen = false)
		{
			try
			{
				DirectoryInfo dirInfo = new DirectoryInfo(ActualPath);
				if (!dirInfo.Exists)
				{
					System.Diagnostics.Debug.WriteLine("Actual Path was not exist. Creating...");
					dirInfo.Create();
					System.Diagnostics.Debug.WriteLine("Actual Path created.");
				}

				DirectoryInfo subdirInfo = new DirectoryInfo(Path.Combine(ActualPath, name));
				if (subdirInfo.Exists)
				{
					System.Diagnostics.Debug.WriteLine("Current subdirectory already created.");
				}
				else
				{
					dirInfo.CreateSubdirectory(name);
					System.Diagnostics.Debug.WriteLine("Created subdirectory \"" + name + "\".");
				}

				if (createThenOpen) DownDirectory(name);
				//else Debug.Log("Actual path: " + ActualPath);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex.Message);
			}
		}
		/// <summary>
		/// Асинхронно копирует директорию и ее содержимое. Имеет смысл при большом количестве подкаталогов и файлов.
		/// При копировании на другой диск использует асинхронное копирование файлов.
		/// </summary>
		/// <param name="from">Полное имя директории</param>
		/// <param name="to">Полное имя новой директории</param>
		/// <param name="callback">Вызывается после завершения копирования</param>
		public async Task CopyDirectoryAsync(string from, string to, Action callback = null)
		{
			await Task.Run(async () =>
			{
				if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || Path.HasExtension(from) || Path.HasExtension(to)) return;
				if (!Directory.Exists(to)) Directory.CreateDirectory(to);
				bool toAnotherDisk = from[0] != to[0];
				DirectoryInfo fromDir = new DirectoryInfo(from);
				if (toAnotherDisk)
				{
					foreach (var item in fromDir.EnumerateFiles())
					{
						await CopyFileAsync(item.FullName, to);
					}
				}
				else
				{
					foreach (var item in fromDir.EnumerateFiles())
					{
						item.CopyTo(Path.Combine(to, item.Name));
					}
				}
				foreach (var item in fromDir.EnumerateDirectories())
				{
					await CopyDirectoryAsync(item.FullName, Path.Combine(to, item.Name));
				}
			});
			if (callback != null) callback();
		}
		/// <summary>
		/// Асинхронно копирует файл. Имеет смысл только при копировании на другой диск больших файлов.
		/// </summary>
		/// <param name="fileName">Полное имя файла</param>
		/// <param name="to">Директория, в которую нужно скопировать файл или полное новое имя файла</param>
		/// <returns></returns>
		public async Task CopyFileAsync(string fileName, string to)
		{
			if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(to) || !Path.HasExtension(fileName)) return;
			await Task.Run(() =>
			{
				bool toFolder = !Path.HasExtension(to);
				if (toFolder)
				{
					File.Copy(fileName, Path.Combine(to, Path.GetFileName(fileName)));
				}
				else
				{
					File.Copy(fileName, to);
				}
			});
		}
		/// <summary>
		/// Удаляет указанный подкаталог.
		/// </summary>
		/// <param name="name">Относительный путь к директории</param>
		public void RemoveDirectory(string name)
		{
			try
			{
				DirectoryInfo dirInfo = new DirectoryInfo(ActualPath + name + "\\");
				if (dirInfo.Exists)
				{
					dirInfo.Delete(true);
					System.Diagnostics.Debug.WriteLine("Directory \"" + name + "\" deleted.");
				}
				else
				{
					System.Diagnostics.Debug.WriteLine("Directory \"" + name + "\" can't be deleted. It is not exist.");
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex.Message);
			}
		}
		/// <summary>
		/// Перемещается в родительскую директорию.
		/// </summary>
		public void UpDirectory()
		{
			if (ActualPath.Length > 4)
			{
				System.Diagnostics.Debug.WriteLine("Open upper directory: " + ActualPath);
				ActualPath = Directory.GetParent(ActualPath).FullName;
			}
			else
			{
				System.Diagnostics.Debug.WriteLine("Can't open upper directory.");
				System.Diagnostics.Debug.WriteLine("Actual path: " + ActualPath);
			}
		}
		/// <summary>
		/// Перемещает в директорию вниз по иерархии. Создает если ее нет
		/// </summary>
		/// <param name="name">Имя директории</param>
		/// <param name="create">Создавать, если не найдена?</param>
		/// <returns>true - директория существует, false - директория не существует или создана</returns>
		public bool DownDirectory(string name, bool create = false)
		{
			try
			{
				System.Diagnostics.Debug.WriteLine("Open Directory \"" + name + "\".");
				DirectoryInfo dirInfo = new DirectoryInfo(Path.Combine(ActualPath, name));
				if (dirInfo.Exists)
				{
					ActualPath = dirInfo.FullName;
					//System.Diagnostics.Debug.WriteLine("Actual path: " + ActualPath);
					return true;
				}
				else if (create)
				{
					System.Diagnostics.Debug.WriteLine("Directory \"" + name + "\" was not exist.");
					CreateDirectory(name, true);
					return false;
				}
				System.Diagnostics.Debug.WriteLine("Can't open directory \"" + name + "\". It is not exist.");
				System.Diagnostics.Debug.WriteLine("Actual path: " + ActualPath);
				return false;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(ex.Message);
				return false;
			}
		}
		/// <summary>
		/// Возвращает относительный путь.
		/// </summary>
		/// <remarks>
		/// <br>Пример:</br>
		/// <br>RelativePath("C:\folder1","C:\folder1\test.exe")  ->  "test.exe"</br>
		/// </remarks>
		/// <param name="fromPath">Относительный путь</param>
		/// <param name="toPath">Полный путь</param>
		public static string RelativePath(string fromPath, string toPath)
		{
			return toPath.Replace(fromPath + @"\", "");
		}
		/// <summary>
		/// Записывает сгенерированный текст в файл
		/// </summary>
		/// <param name="index">Содержимое индекса</param>
		/// <param name="fileName">Название файла</param>
		public async void WriteTextFile(string fileName, string index)
		{
			try
			{
				System.IO.File.WriteAllText(Path.Combine(ActualPath, fileName), index);
			}
			catch (Exception)
			{
				await Task.Delay(10);
				WriteTextFile(fileName, index);
			}
		}
		/// <summary>
		/// Записывает сгенерированный индекс в файл
		/// </summary>
		/// <param name="index">Содержимое индекса</param>
		/// <param name="language">Язык файла</param>
		public async void WriteIndex(string index, SystemLanguage language)
		{
			try
			{
				if (language == SystemLanguage.Russian)
				{
					System.IO.File.WriteAllText(Path.Combine(ActualPath, $"index.txt"), index);
				}
				else
				{
					System.IO.File.WriteAllText(Path.Combine(ActualPath, $"index_{LangugeUtils.GetLanguagePrefix(language)}.txt"), index);
				}
			}
			catch (Exception)
			{
				await Task.Delay(10);
				WriteIndex(index, language);
			}
		}
		/// <summary>
		/// Получает текст index файла.
		/// </summary>
		/// <param name="language">Язык файла</param>
		/// <returns>Текст файла</returns>
		public async Task<string> ReadIndex(SystemLanguage language = SystemLanguage.Russian)
		{
			System.Diagnostics.Debug.WriteLine("Reading index at: " + ActualPath);
			if (File.Exists(Path.Combine(ActualPath, $"index_{LangugeUtils.GetLanguagePrefix(language)}.txt")))
			{
				try
				{
					return File.ReadAllText(Path.Combine(ActualPath, $"index_{LangugeUtils.GetLanguagePrefix(language)}.txt"));
				}
				catch (Exception)
				{
					await Task.Delay(10);
					return await ReadIndex(language);
				}
			}

			if (language == SystemLanguage.Russian && File.Exists(Path.Combine(ActualPath, $"index.txt")))
			{
				try
				{
					return File.ReadAllText(Path.Combine(ActualPath, $"index.txt"));
				}
				catch (Exception)
				{
					await Task.Delay(10);
					return await ReadIndex(language);
				}
			}

			return null;
		}
		/// <summary>
		/// Получает весь текст из файла.
		/// </summary>
		/// <param name="fileName">Относительный путь к файлу</param>
		/// <returns>Текст файла</returns>
		public async Task<string> ReadFile(string fileName)
		{
			if (File.Exists(Path.Combine(ActualPath, $"{fileName}")))
			{
				try
				{
					return File.ReadAllText(Path.Combine(ActualPath, $"{fileName}"));
				}
				catch (Exception)
				{
					await Task.Delay(10);
					return await ReadFile(fileName);
				}
			}
			System.Diagnostics.Debug.WriteLine("File \"" + Path.Combine(ActualPath, $"{fileName}") + "\" was not exist.");
			return null;
		}
		/// <summary>
		/// Удаляет файл.
		/// </summary>
		/// <param name="name">Относительный путь к файлу</param>
		public void DeleteFile(string name)
		{
			if(!name.Contains(':')) // локальный путь
			{
				name = Path.Combine(ActualPath, name);
			}
			if (File.Exists(name))
			{
				File.Delete(name);
			}
		}
		/// <summary>
		/// Получает список полных имен подкаталогов.
		/// </summary>
		/// <returns>Список полных имен</returns>
		public string[] GetDirectories()
		{
			System.Diagnostics.Debug.WriteLine("Getting directories for: " + ActualPath);
			return Directory.EnumerateDirectories(ActualPath).ToArray();
		}
		/// <summary>
		/// Получает список имен подкаталогов.
		/// </summary>
		/// <returns>Список имен подкаталогов</returns>
		public string[] GetDirectoriesNames()
		{
			System.Diagnostics.Debug.WriteLine("Getting directories for: " + ActualPath);
			DirectoryInfo[] dirs = new DirectoryInfo(ActualPath).GetDirectories();
			string[] names = new string[dirs.Length];
			for (int i = 0; i < names.Length; i++)
			{
				names[i] = dirs[i].Name;
			}
			return names;
		}
		/// <summary>
		/// Получает список имен файлов и подкаталогов.
		/// </summary>
		/// <returns>Список имен файлов и подкаталогов</returns>
		public string[] GetFilesAndDirectoriesNames()
		{
			System.Collections.Generic.List<string> result = new System.Collections.Generic.List<string>(GetDirectoriesNames());
			DirectoryInfo dir = new DirectoryInfo(ActualPath);
			foreach (var item in dir.EnumerateFiles())
			{
				result.Add(item.Name);
			}
			return result.ToArray();
		}
		/// <summary>
		/// Получает текущую директорию в виде объекта DirectoryInfo.s
		/// </summary>
		public DirectoryInfo GetDirectoryInfo()
		{
			return new DirectoryInfo(ActualPath);
		}
		/// <summary>
		/// Проверяет _actualPath на заполненность и если что-то не так - инициализирует его
		/// </summary>
		private void CheckDirForInitialized()
		{
			if (string.IsNullOrEmpty(_actualPath))
			{
				_actualPath = AppDomain.CurrentDomain.BaseDirectory;
				System.Diagnostics.Debug.WriteLine(_actualPath);
			}
		}
		/// <summary>
		/// Удаляет файл индекса в текущей директории, если он есть
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public int RemoveIndex() //+
		{
			FileInfo[] files = GetDirectoryInfo().GetFiles();
			foreach (var item in files)
			{
				if (item.Name.StartsWith("index"))
				{
					try
					{
						item.Delete();
					}
					catch { return -1; }
					return 1;
				}
			}
			return 0;
		}
		/// <summary>
		/// Удаляет видео в текущей директории, если оно есть
		/// </summary>
		/// <param name="all">true - удаление всех видеофайлов, false - удаление первого найденного видеофайла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public int RemoveVideo(bool all = false)
		{
			string ext;
			bool br = false;
			int count = 0;
			foreach (var item in GetDirectoryInfo().EnumerateFiles())
			{
				ext = Path.GetExtension(item.Name).ToLower();
				switch (ext)
				{
					case ".asf":
					case ".avi":
					case ".dv":
					case ".m4v":
					case ".mov":
					case ".mp4":
					case ".mpg":
					case ".mpeg":
					case ".ogv":
					case ".vp8":
					case ".webm":
					case ".wmv":
						count++;
						try
						{
							item.Delete();
						}
						catch { return -1; }
						br = true;
						break;
					default:
						break;
				}
				if (!all && br) break;
			}
			return count;
		}
		/// <summary>
		/// Удаляет файл изображения в текущей директории, если он есть
		/// </summary>
		/// <param name="all">true - удаление всех файлов, false - удаление первого найденного файла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public int RemoveImage(bool all = false)
		{
			string ext;
			bool br = false;
			int count = 0;
			foreach (var item in GetDirectoryInfo().EnumerateFiles())
			{
				ext = Path.GetExtension(item.Name).ToUpper();
				switch (ext)
				{
					case ".PNG":
					case ".JPG":
					case ".JPEG":
					case ".TIFF":
					case ".BMP":
					case ".EXR":
					case ".GIF":
					case ".HDR":
					case ".IFF":
					case ".PICT":
					case ".PSD":
					case ".TGA":
						count++;
						try
						{
							item.Delete();
						}
						catch { return -1; }
						br = true;
						break;
					default:
						break;
				}
				if (!all && br) break;
			}
			return count;
		}
		/// <summary>
		/// Удаляет аудио файл в текущей директории, если он есть
		/// </summary>
		/// <param name="all">true - удаление всех файлов, false - удаление первого найденного файла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public int RemoveAudio(bool all = false) //+
		{
			string ext;
			bool br = false;
			int count = 0;
			foreach (var item in GetDirectoryInfo().EnumerateFiles())
			{
				ext = Path.GetExtension(item.Name).ToLower();
				switch (ext)
				{
					case ".mp3":
					case ".wav":
					case ".ogg":
					case ".aiff ":
					case ".aif":
					case ".mod":
					case ".it":
					case ".s3m":
					case ".xm":
						count++;
						try
						{
							item.Delete();
						}
						catch { return -1; }
						br = true;
						break;
					default:
						break;
				}
				if (!all && br) break;
			}
			return count;
		}
		/// <summary>
		/// Удаляет все файлы видео в текущей директории
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public int RemoveVideos()
		{
			return RemoveVideo(true);
		}
		/// <summary>
		/// Удаляет все файлы изображений в текущей директории
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public int RemoveImages()
		{
			return RemoveImage(true);
		}
		/// <summary>
		/// Удаляет все файлы аудио в текущей директории
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public int RemoveAudios()
		{
			return RemoveAudio(true);
		}
		/// <summary>
		/// Копирует текущую директорию в буфер обмена.
		/// </summary>
		public void Copy()
		{
			DirClipboard.Copy(ActualPath);
		}
		/// <summary>
		/// Копирует директорию в буфер обмена.
		/// </summary>
		/// <param name="name">Относительный путь к директории</param>
		public void Copy(string name)
		{
			DirClipboard.Copy(Path.Combine(ActualPath, name));
		}
		/// <summary>
		/// Вставляет контент из буфера обмена.
		/// Поддерживает вставку с подключенного по сети устройства.
		/// </summary>
		public async Task Paste()
		{
			string path = DirClipboard.Paste();
			if (string.IsNullOrEmpty(path)) return;
			bool isFile = Path.HasExtension(path);
			if (path.StartsWith("%remote%/"))
			{
				if (isFile)
				{
					await serverConnection.Download(path.Substring(9));
				}
				else
				{
					string oldPath = await serverConnection.PrintWorkingDirectory();
					path = path.Substring(9);
					string[] pathDirs = path.Split('\\');
					await serverConnection.DownloadDirectory(pathDirs[pathDirs.Length - 1], true);
					await serverConnection.ChangeDir(oldPath);
				}
			}
			else
			{
				string from = path.Substring(9);
				string to = ActualPath;
				if (isFile)
				{
					File.Copy(from, to, true);
				}
				else
				{
					await CopyDirectoryAsync(from, to);
				}
			}
		}
		/// <summary>
		/// Определяет, является ли путь относительным.
		/// </summary>
		/// <param name="path">Путь для проверки</param>
		public static bool IsRelativePath(string path)
		{
			int length = path.Length > 32 ? 32 : path.Length; // 32 - макс длина имени диска в NTFS
			for (int i = 0; i < length; i++)
			{
				if(path[i] == ':') return false;
			}
			return true;
		}
		/// <summary>
		/// Синхронизирует текущую директорию с директорией на подключенном по сети устройстве.
		/// </summary>
		public async Task Sync()
		{
			await serverConnection.SyncFolder(null);
		}
		/// <summary>
		/// Синхронизирует директорию с директорией на подключенном по сети устройстве.
		/// </summary>
		/// <param name="path">Относительный путь к директории</param>
		public async Task Sync(string path)
		{
			await serverConnection.SyncFolder(path);
		}
		#endregion
	}


	public enum SystemLanguage
	{
		Afrikaans,
		Arabic,
		Basque,
		Belarusian,
		Bulgarian,
		Catalan,
		Chinese,
		Czech,
		Danish,
		Dutch,
		English,
		Estonian,
		Faroese,
		Finnish,
		French,
		German,
		Greek,
		Hebrew,
		Hungarian,
		Icelandic,
		Indonesian,
		Italian,
		Japanese,
		Korean,
		Latvian,
		Lithuanian,
		Norwegian,
		Polish,
		Portuguese,
		Romanian,
		Russian,
		SerboCroatian,
		Slovak,
		Slovenian,
		Spanish,
		Swedish,
		Thai,
		Turkish,
		Ukrainian,
		Vietnamese
	}
	enum LanguagePostfix : byte
	{
		_en = SystemLanguage.English,
		_ru = SystemLanguage.Russian
	}
	public static class LangugeUtils
	{
		public static string GetLanguagePrefix(SystemLanguage systemLanguage)
		{
			switch (systemLanguage)
			{
				case SystemLanguage.Afrikaans: return "af";
				case SystemLanguage.Arabic: return "ar";
				case SystemLanguage.Basque: return "eu";
				case SystemLanguage.Belarusian: return "be";
				case SystemLanguage.Bulgarian: return "bg";
				case SystemLanguage.Catalan: return "ca";
				case SystemLanguage.Chinese: return "zh";
				case SystemLanguage.Czech: return "cs";
				case SystemLanguage.Danish: return "da";
				case SystemLanguage.Dutch: return "nl";
				case SystemLanguage.English: return "en";
				case SystemLanguage.Estonian: return "et";
				case SystemLanguage.Faroese: return "fo";
				case SystemLanguage.Finnish: return "fi";
				case SystemLanguage.French: return "fr";
				case SystemLanguage.German: return "de";
				case SystemLanguage.Greek: return "el";
				case SystemLanguage.Hebrew: return "he";
				case SystemLanguage.Hungarian: return "hu";
				case SystemLanguage.Icelandic: return "is";
				case SystemLanguage.Indonesian: return "id";
				case SystemLanguage.Italian: return "it";
				case SystemLanguage.Japanese: return "ja";
				case SystemLanguage.Korean: return "ko";
				case SystemLanguage.Latvian: return "lv";
				case SystemLanguage.Lithuanian: return "lt";
				case SystemLanguage.Norwegian: return "no";
				case SystemLanguage.Polish: return "pl";
				case SystemLanguage.Portuguese: return "pt";
				case SystemLanguage.Romanian: return "ro";
				case SystemLanguage.Russian: return "ru";
				case SystemLanguage.SerboCroatian: return "sr";
				case SystemLanguage.Slovak: return "sk";
				case SystemLanguage.Slovenian: return "sl";
				case SystemLanguage.Spanish: return "es";
				case SystemLanguage.Swedish: return "sv";
				case SystemLanguage.Thai: return "th";
				case SystemLanguage.Turkish: return "tr";
				case SystemLanguage.Ukrainian: return "uk";
				case SystemLanguage.Vietnamese: return "vi";
				default: return null;
			}
		}
		public static string GetLangPostfix(SystemLanguage _lang) => typeof(LanguagePostfix).GetEnumNames()[(int)_lang];
	}
}

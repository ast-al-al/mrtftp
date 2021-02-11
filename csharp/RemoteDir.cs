using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MRT.Network.FTP
{
	public class RemoteDir
	{
		public AsyncServerConnection serverConnection;
		private string _stringNameOfActivePanel = null; // caсhe
		public RemoteDir(AsyncServerConnection serverConnection)
		{
			this.serverConnection = serverConnection;
		}
		/// <summary>
		/// Запрашивает имя подключенной панели.
		/// <br>Значение кэшируется до перезапуска программы!</br>
		/// </summary>
		/// <returns>Имя подключенной панели. Null, если файлы панели не найдены</returns>
		public async Task<string> StringNameOfActivePanel()
		{
			if (_stringNameOfActivePanel != null) return _stringNameOfActivePanel;
			return _stringNameOfActivePanel = await serverConnection.GetPanelName();
		}
		/// <summary>
		/// Запрашивает полный путь к текущей удаленной папке.
		/// </summary>
		/// <returns>Полный путь к текущей удаленной папке</returns>
		public async Task<string> GetActualPath() //+
		{
			return await serverConnection.PrintWorkingDirectory();
		}
		/// <summary>
		/// Запрашивает переход в новую рабочую директорию.
		/// </summary>
		/// <param name="path">Путь к директории</param>
		public async Task SetActualPath(string path) //+
		{
			await serverConnection.ChangeDir(path);
		}
		/// <summary>
		/// Запрашивает удаление всех файлов и папок в директории.
		/// </summary>
		/// <param name="path">Относительный путь к директории. Оставить пустым для очистки текущей директории</param>
		public async Task ClearDir(string path) //+
		{
			await serverConnection.ClearDirectory(path);
		}
		/// <summary>
		/// Запрос на переход в папку StreamingAssets.
		/// </summary>
		/// <returns>Путь к директории StreamingAssets, DIR_NOT_FOUND если директория не найдена</returns>
		public async Task<string> GoToStreamingAssets()
		{
			string resp = await serverConnection.SendCommandAsync("GTSA");
			string respCode = resp.Substring(0, 3);
			if (respCode == "550") return "DIR_NOT_FOUND";
			else return resp.Substring(resp.IndexOf('#') + 1);
		}
		/// <summary>
		/// Запрос на создание директории.
		/// </summary>
		/// <param name="name">Имя директории для создания</param>
		/// <param name="createThenOpen">Перейти в директорию после создания?</param>
		public async Task CreateDirectory(string name, bool createThenOpen = false)
		{
			await serverConnection.MakeDir(name);
			if (createThenOpen) await serverConnection.ChangeDir(name);
		}
		/// <summary>
		/// Запрос на удаление директории.
		/// </summary>
		/// <param name="name">Относительный путь к директории для удаления</param>
		public async Task RemoveDirectory(string name)
		{
			await serverConnection.RemoveDir(name);
		}
		/// <summary>
		/// Запрос на переход в родительскую директорию.
		/// </summary>
		public async Task UpDirectory()
		{
			await serverConnection.SendCommandAsync("CDUP");
		}
		/// <summary>
		/// Запрос на переход в дочернюю директорию.
		/// </summary>
		/// <param name="name">Имя директории</param>
		public async Task DownDirectory(string name)
		{
			await serverConnection.ChangeDir(name);
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
		/// Запрос на удаление файла.
		/// </summary>
		/// <param name="name">Относительый путь к файлу</param>
		public async Task DeleteFile(string name)
		{
			await serverConnection.DeleteFile(name);
		}
		/// <summary>
		/// Запрос на создание текстового файла.
		/// </summary>
		/// <param name="fileName">Имя файла (можно без расширения)</param>
		/// <param name="text">Текст, который запишется в файл</param>
		public async Task WriteTextFile(string fileName, string text)
		{
			if (!Path.HasExtension(fileName)) fileName += ".txt";
			string tempFileName = Path.Combine(serverConnection.LocalDir.ActualPath, $"{fileName}_{text.GetHashCode()}.txt");
			using (FileStream fs = File.Create(tempFileName))
			{
				byte[] bytes = Encoding.UTF8.GetBytes(text);
				await fs.WriteAsync(bytes, 0, bytes.Length);
				fs.Close();
			}
			await serverConnection.Upload(tempFileName.Substring(0, tempFileName.LastIndexOf('_')));
			File.Delete(tempFileName);
		}
		/// <summary>
		/// Запрос на получение текста index файла.
		/// </summary>
		/// <param name="language">Язык индекса</param>
		/// <returns>Текст индекса. Null, если файл индекса не найден</returns>
		public async Task<string> ReadIndex(SystemLanguage language = SystemLanguage.Russian)
		{
			string name = $"index_{LangugeUtils.GetLanguagePrefix(language)}.txt";
			string[] lst = await serverConnection.List(true);
			foreach (var item in lst)
			{
				if (item == name || (language == SystemLanguage.Russian && item == "index.txt"))
				{
					string newName = name + (await serverConnection.GetFileSize(name)).ToString();
					await serverConnection.Download(name, newName);
					string text;
					using (StreamReader sr = new StreamReader(Path.Combine(serverConnection.LocalDir.ActualPath, newName)))
					{
						text = await sr.ReadToEndAsync();
						sr.Close();
					}
					return text;
				}
			}
			return null;
		}
		/// <summary>
		/// Запрос на получения списка имен директорий в текущей директории.
		/// </summary>
		/// <returns>Список имен директорий в текущей директории</returns>
		public async Task<string[]> GetDirectoriesNames()
		{
			string[] response = await serverConnection.List(true);
			List<string> result = new List<string>();
			foreach (var item in response)
			{
				if (!Path.HasExtension(item)) result.Add(item);
			}
			return result.ToArray();
		}
		/// <summary>
		/// Запрос на получение списка имен директорий и файлов в текущей директории.
		/// </summary>
		/// <returns>Список имен директорий и файлов в текущей директории</returns>
		public async Task<string[]> GetFilesAndDirectoriesNames()
		{
			string[] files = await serverConnection.List(true);
			return files;
		}
		/// <summary>
		/// Запрос на получение списка имен директорий и файлов в текущей директории.
		/// <remarks>
		/// <br>Пример:</br>
		/// <br>"d*mm dd yyyy*dir_name" - для директории</br>
		/// <br>"f*mm dd yyyy*file_name.extension" - для файла</br>
		/// </remarks>
		/// </summary>
		/// <returns>Список имен директорий в текущей директории</returns>
		public async Task<string[]> GetFilesAndDirectoriesNamesExtended()
		{
			string[] files = await serverConnection.ExtendedList();
			return files;
		}
		/// <summary>
		/// Запрос на удаление индекса в текущей директории.
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public async Task<int> RemoveIndex() //+
		{
			string result = await serverConnection.SendCommandAsync("RIN");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление видео в текущей директории, если оно есть.
		/// </summary>
		/// <param name="all">true - удаление всех видеофайлов, false - удаление первого найденного видеофайла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public async Task<int> RemoveVideo(bool all = false) //+
		{
			string result = await serverConnection.SendCommandAsync(all ? "RVDS" : "RVD");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление файла изображения в текущей директории, если он есть.
		/// </summary>
		/// <param name="all">true - удаление всех файлов, false - удаление первого найденного файла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public async Task<int> RemoveImage(bool all = false) //+
		{
			string result = await serverConnection.SendCommandAsync(all ? "RIMS" : "RIM");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление аудио файла в текущей директории, если он есть.
		/// </summary>
		/// <param name="all">true - удаление всех файлов, false - удаление первого найденного файла</param>
		/// <returns>Количество удаленных файлов. -1 - файл недоступен</returns>
		public async Task<int> RemoveAudio(bool all = false) //+
		{
			string result = await serverConnection.SendCommandAsync(all ? "RAUS" : "RAU");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление всех файлов видео в текущей директории.
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public async Task<int> RemoveVideos() //+
		{
			string result = await serverConnection.SendCommandAsync("RVDS");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление всех файлов изображений в текущей директории.
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public async Task<int> RemoveImages() //+
		{
			string result = await serverConnection.SendCommandAsync("RIMS");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Запрос на удаление всех файлов аудио в текущей директории.
		/// </summary>
		/// <returns>Количество удаленных файлов. -1 если один из файлов недоступен</returns>
		public async Task<int> RemoveAudios() //+
		{
			string result = await serverConnection.SendCommandAsync("RAUS");
			return int.Parse(result.Split(' ')[1]);
		}
		/// <summary>
		/// Копирует текущую удаленную директорию для последующей вставки либо на локальную машину либо на удаленную.
		/// </summary>
		/// <returns></returns>
		public async Task Copy()
		{
			DirClipboard.Copy($"%remote%/{await GetActualPath()}");
		}
		/// <summary>
		/// Копирует удаленную директорию или файл для последующей вставки либо на локальную машину либо на удаленную.
		/// </summary>
		/// <param name="name">Относительный путь к файлу или папке</param>
		/// <returns></returns>
		public async Task Copy(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				await Copy();
				return;
			}
			DirClipboard.Copy($"%remote%/{Path.Combine(await GetActualPath(), name)}");
		}
		/// <summary>
		/// Вставляет скопированные файлы и папки в текущую удаленную директорию.
		/// </summary>
		/// <returns></returns>
		public async Task Paste()
		{
			string path = DirClipboard.Paste();
			if (string.IsNullOrEmpty(path)) return;
			if (path.StartsWith("%remote%/"))
			{
				string from = path.Substring(9);
				string to = await GetActualPath();
				string resp = await serverConnection.SendCommandAsync($"CFR {from}");
				if (resp.StartsWith("200")) // OK
				{
					await serverConnection.SendCommandAsync($"CTO {to}");
				}
			}
			else
			{
				bool isFile = Path.HasExtension(path);
				if (isFile)
				{
					await serverConnection.Upload(path);
				}
				else
				{
					await serverConnection.UploadDirectory(path, true);
				}
			}
		}
	}
}

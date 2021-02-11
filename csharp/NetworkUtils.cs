using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MRT.Network.FTP
{
	public static class NetworkUtils
	{
		/// <summary>
		/// Асинхронно отправляет массив байтов на сокет.
		/// </summary>
		/// <returns>Количество отправленных байт</returns>
		public static async Task<int> SendPacketAsync(Socket socket, byte[] buffer, int offset, int length, SocketFlags flags)
		{
			return await Task.Factory.FromAsync
				(
					socket.BeginSend(buffer, offset, length, flags, null, socket),
					socket.EndSend
				).ConfigureAwait(false);
		}
		/// <summary>
		/// Асинхронно принимает UTF8 строку через сокет.
		/// </summary>
		/// <returns>Полученная строка</returns>
		public static async Task<string> RecieveStringAsync(Socket socket, int bufferSize)
		{
			byte[] buffer = new byte[bufferSize];
			await RecievePacketAsync(socket, buffer);
			string result = Encoding.UTF8.GetString(buffer);
			return result;
		}
		/// <summary>
		/// Асинхронно принимает массив байтов через сокет.
		/// </summary>
		/// <returns>Количество полученных байтов</returns>
		public static async Task<int> RecievePacketAsync(Socket socket, byte[] buffer)
		{
			return await Task.Factory.FromAsync
				(
					socket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, null, socket),
					socket.EndSend
				).ConfigureAwait(false);
		}
		/// <summary>
		/// Асинхронно отправляет файл на сокет.
		/// <param name="path">Полный путь к файлу</param>
		/// </summary>
		public static async Task SendFileToSocketAsync(string path, Socket socket, int bufferSize)
		{
			byte[] bytes = new byte[bufferSize];
			using (FileStream fs = new FileStream(path, FileMode.Open))
			{
				long size = fs.Length;
				long sended = 0;
				int length;
				while (await fs.ReadAsync(bytes, 0, bytes.Length) > 0)
				{
					if (sended + bytes.Length > size) length = (int)(size - sended);
					else length = bytes.Length;
					sended += await SendPacketAsync(socket, bytes, 0, length, SocketFlags.None);
					//OnProgressChanged(sended / size);
				}
				fs.Close();
			}
		}
		/// <summary>
		/// Асинхронно записывает файл из сокета.
		/// </summary>
		/// <param name="pathToWrite">Полный путь к файлу для записи</param>
		public static async Task WriteFileFromSocketAsync(string pathToWrite, Socket socket, int bufferSize)
		{
			if (pathToWrite.Length > 250) pathToWrite = @"\\?\" + pathToWrite;
			string folder;
			byte[] buffer = new byte[bufferSize];
			int length;
			folder = Path.GetDirectoryName(pathToWrite);
			Directory.CreateDirectory(folder);
			using (FileStream fs = new FileStream(pathToWrite, FileMode.OpenOrCreate))
			{
				while ((length = await RecievePacketAsync(socket, buffer)) > 0)
				{
					await fs.WriteAsync(buffer, 0, length);
				}
				fs.Close();
			}
		}
		/// <summary>
		/// Асинхронно отправляет файл в сетевой поток.
		/// </summary>
		/// <param name="pathToFile">Полный путь к файлу</param>
		public static async Task SendFileToNetworkStream(string pathToFile, NetworkStream stream, int bufferSize)
		{
			using (FileStream fs = new FileStream(pathToFile, FileMode.Open))
			{
				byte[] buffer = new byte[bufferSize];
				long size = fs.Length;
				long sended = 0;
				int length;
				while (await fs.ReadAsync(buffer, 0, buffer.Length) > 0)
				{
					if (sended + buffer.Length > size) length = (int)(size - sended);
					else length = buffer.Length;
					sended += length;
					await stream.WriteAsync(buffer, 0, length);
				}
				await stream.FlushAsync();
				fs.Close();
			}
		}
		/// <summary>
		/// Асинхронно записывает файл из сетевого потока.
		/// </summary>
		/// <param name="pathToFile">Полный путь к файлу для записи</param>
		/// <returns></returns>
		public static async Task WriteFileFromNetworkStream(string pathToFile, NetworkStream stream, int bufferSize)
		{
			try
			{
				using (FileStream fs = new FileStream(pathToFile, FileMode.OpenOrCreate))
				{
					byte[] buffer = new byte[bufferSize];
					int length;
					while ((length = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
					{
						await fs.WriteAsync(buffer, 0, length);
					}
					await fs.FlushAsync();
					fs.Close();
				}
			}
			catch
			{

			}
		}
		/// <summary>
		/// Получает IP адрес устройства в локальной сети.
		/// </summary>
		/// <returns>IP адрес устройства в локальной сети</returns>
		public static string GetLocalIP_1()
		{
			string localIP;
			using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
			{
				socket.Connect("8.8.8.8", 65530);
				IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
				localIP = endPoint.Address.ToString();
			}
			return localIP;
		}
		/// <summary>
		/// Получает список возможных IP адресов устройства в локальной сети.
		/// </summary>
		/// <returns>Возможные IP адреса устройства в локальной сети</returns>
		public static string[] GetLocalIP_2(NetworkInterfaceType _type)
		{
			List<string> ipAddrList = new List<string>();
			foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (item.NetworkInterfaceType == _type && item.OperationalStatus == OperationalStatus.Up)
				{
					foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
					{
						if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
						{
							ipAddrList.Add(ip.Address.ToString());
						}
					}
				}
			}
			return ipAddrList.ToArray();
		}
	}
}

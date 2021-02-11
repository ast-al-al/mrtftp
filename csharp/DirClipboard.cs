namespace MRT.Network.FTP
{
	/// <summary>
	/// Буфер обмена между локальнымии удаленными директориями.
	/// </summary>
	static class DirClipboard
	{
		private static string clipboard;
		/// <summary>
		/// Сохраняет строку в буфер обмена.
		/// </summary>
		/// <param name="data"></param>
		public static void Copy(string data)
		{
			clipboard = data;
		}
		/// <summary>
		/// Получает строку из буфера обмена.
		/// </summary>
		/// <returns></returns>
		public static string Paste()
		{
			return clipboard;
		}
	}
}


namespace MRT.Debug
{
	/// <summary>
	/// Универсальный инструмент вывода сообщений в консоль. Поддерживает работу в Unity.
	/// </summary>
	public static class XDebug
	{
		/// <summary>
		/// Цвета для вывода во все виды консолей.
		/// </summary>
		public enum DebugColors { White, Green, Red, Yellow, Gray, Default }

#if UNITY_EDITOR || UNITY_STANDALONE
		private static System.Collections.Generic.Dictionary<DebugColors, string> stringColors = new System.Collections.Generic.Dictionary<DebugColors, string>()
		{
			{DebugColors.Gray, "<color=grey>" },
			{DebugColors.Green, "<color=green>" },
			{DebugColors.Red, "<color=red>" },
			{DebugColors.Yellow, "<color=yellow>" },
			{DebugColors.White, "<color=white>" },
			{DebugColors.Default, "" },
		};
#else
		private static readonly System.Collections.Generic.Dictionary<DebugColors, System.ConsoleColor> consoleColors = new System.Collections.Generic.Dictionary<DebugColors, System.ConsoleColor>()
		{
			{DebugColors.Gray, System.ConsoleColor.Gray },
			{DebugColors.Green, System.ConsoleColor.Green },
			{DebugColors.Red, System.ConsoleColor.Red },
			{DebugColors.Yellow, System.ConsoleColor.Yellow },
			{DebugColors.White, System.ConsoleColor.White },
		};
#endif
		/// <summary>
		/// Выводит сообщение в консоль.
		/// </summary>
		/// <param name="message">Текст сообщения</param>
		/// <param name="color">Цвет сообщения</param>
		/// <param name="toConsole">Выводить текст в окно коноли? (только для C# ConsoleApp)</param>
		/// <param name="printDateTime">Вставить время и дату перед сообщением?</param>
		public static void Log(string message, DebugColors color = DebugColors.Default, bool toConsole = false, bool printDateTime = true)
		{
			if (printDateTime) message = System.DateTime.Now.ToString() + ": " + message;
#if UNITY_EDITOR || UNITY_STANDALONE
			UnityEngine.Debug.Log($"{stringColors[color]}{message}{color == DebugColors.Default ? "" : "</color>"}");
#else
			if (toConsole)
			{
				if (color != DebugColors.Default)
				{
					System.ConsoleColor oldColor = System.Console.ForegroundColor;
					System.Console.ForegroundColor = consoleColors[color];
					System.Console.WriteLine(message);
					System.Console.ForegroundColor = oldColor;
				}
				else
				{
					System.Console.WriteLine(message);
				}
			}
			else
			{
				System.Diagnostics.Debug.WriteLine(message);
			}
#endif
		}
	}
}

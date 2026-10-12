using System;
using System.Collections.Generic;
using System.Text;
using Codebase.Configs;
using Unity.Collections;
using Unity.Netcode;

namespace Codebase;

public static class NetworkCommunication
{
	private static readonly HashSet<string> DataNamesToIgnore = new HashSet<string>();

	public static void AddToNotLogList(ICollection<string> dataNamesToNotLog)
	{
		DataNamesToIgnore.UnionWith(dataNamesToNotLog);
	}

	public static void RemoveFromNotLogList(ICollection<string> dataNamesToNotLog)
	{
		foreach (string item in dataNamesToNotLog)
		{
			DataNamesToIgnore.Remove(item);
		}
	}

	public static List<string> GetDataNamesToIgnore()
	{
		return new List<string>(DataNamesToIgnore);
	}

	public static void SendData(string dataName, string dataStr, ulong clientId, string listener, IConfig config, NetworkDelivery networkDelivery = NetworkDelivery.ReliableFragmentedSequenced)
	{
		try
		{
			if (dataName == null)
			{
				dataName = "";
			}
			if (dataStr == null)
			{
				dataStr = "";
			}
			byte[] bytes = Encoding.UTF8.GetBytes(dataStr);
			int num = Encoding.UTF8.GetByteCount(dataName) + 8 + bytes.Length;
			if (num <= 0 || num > 65536)
			{
				Logging.LogError($"SendData: invalid size {num}, skipping send", config);
				return;
			}
			using FastBufferWriter messageStream = new FastBufferWriter(num, Allocator.Temp);
			messageStream.WriteValue(dataName, oneByteChars: true);
			messageStream.WriteBytes(bytes);
			NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(listener, clientId, messageStream, networkDelivery);
			if (Logging.IsEnabled(config) && !DataNamesToIgnore.Contains(dataName))
			{
				Logging.Log($"Sent data \"{dataName}\" ({bytes.Length} bytes - {num} total bytes) to {clientId} with listener {listener}.", config);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error when writing streamed data: {arg}", config);
		}
	}

	public static void SendDataToAll(string dataName, string dataStr, string listener, IConfig config, NetworkDelivery networkDelivery = NetworkDelivery.ReliableFragmentedSequenced)
	{
		try
		{
			if (dataName == null)
			{
				dataName = "";
			}
			if (dataStr == null)
			{
				dataStr = "";
			}
			byte[] bytes = Encoding.UTF8.GetBytes(dataStr);
			int num = Encoding.UTF8.GetByteCount(dataName) + 8 + bytes.Length;
			if (num <= 0 || num > 65536)
			{
				Logging.LogError($"SendDataToAll: invalid size {num} (nameBytes + 8 + dataLen), skipping send", config);
				return;
			}
			using FastBufferWriter messageStream = new FastBufferWriter(num, Allocator.Temp);
			messageStream.WriteValue(dataName, oneByteChars: true);
			messageStream.WriteBytes(bytes);
			NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(listener, messageStream, networkDelivery);
			if (Logging.IsEnabled(config) && !DataNamesToIgnore.Contains(dataName))
			{
				Logging.Log($"Sent data \"{dataName}\" ({bytes.Length} bytes - {num} total bytes) to all clients with listener {listener}.", config);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error when writing streamed data: {arg}", config);
		}
	}

	public static (string DataName, string DataStr) GetData(ulong clientId, FastBufferReader reader, IConfig config)
	{
		try
		{
			reader.ReadValue(out var s, oneByteChars: true);
			int num = reader.Length - reader.Position;
			int num2 = num + 8 + Encoding.UTF8.GetByteCount(s);
			byte[] array = new byte[num];
			reader.ReadBytesSafe(ref array, num);
			string text = Encoding.UTF8.GetString(array).Trim();
			s = s.Trim();
			if (Logging.IsEnabled(config) && !DataNamesToIgnore.Contains(s))
			{
				Logging.Log(string.Format("Received data {0} ({1} bytes - {2} total bytes) from {3}. Content : {4}", s, num, num2, (clientId == 0L) ? "server" : clientId.ToString(), text), config);
			}
			return (DataName: s, DataStr: text);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error when reading streamed data: {arg}", config);
		}
		return (DataName: "", DataStr: "");
	}
}

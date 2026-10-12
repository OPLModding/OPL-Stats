using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using Codebase;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OPLStats_Stats;

public partial class Stats
{
	private sealed class RemoteUploadSkipException : Exception
	{
		public RemoteUploadSkipException(string message)
			: base(message)
		{
		}
	}

	private sealed class RemoteHttpResponse
	{
		public int StatusCode { get; set; }

		public string StatusText { get; set; } = "";

		public string Body { get; set; } = "";
	}

	private sealed class RemoteGameScheduleInfo
	{
		public string GameId { get; set; } = "";

		public string RawResponseJson { get; set; } = "";
	}

	private sealed class RemoteTeamResponse
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";
	}

	private sealed class RemoteGameScheduleResponse
	{
		public string GameId { get; set; } = "";

		public string SeriesId { get; set; } = "";

		public int? Week { get; set; }

		public int? GameIndex { get; set; }

		public string Home { get; set; } = "";

		public string Away { get; set; } = "";

		public string Error { get; set; } = "";

		public RemoteTeamResponse HomeTeam { get; set; }

		public RemoteTeamResponse AwayTeam { get; set; }
	}

	private sealed class RemoteGameUploadResponse
	{
		public string GameId { get; set; } = "";

		public int? HomeScore { get; set; }

		public int? AwayScore { get; set; }

		public string HomeTeamColor { get; set; } = "";

		public int? TotalPlayers { get; set; }

		public string Error { get; set; } = "";
	}

	private static readonly object _remoteUploadSync = new object();

	private static readonly HashSet<string> _remoteUploadsInProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> _remoteUploadsCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, RemoteGameScheduleInfo> _remoteSchedulesByGameReferenceId = new Dictionary<string, RemoteGameScheduleInfo>(StringComparer.OrdinalIgnoreCase);

	private static void TryQueueRemoteGameUpload(string statsJson, string gameReferenceId, string exportFileStem)
	{
		if (!ServerFunc.IsDedicatedServer() || string.IsNullOrWhiteSpace(statsJson) || string.IsNullOrWhiteSpace(gameReferenceId) || !ServerConfig.EnableRemoteUpload)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(ServerConfig.RemoteUploadBaseUrl))
		{
			Logging.Log("Remote upload enabled, but RemoteUploadBaseUrl is empty. Skipping upload.", ServerConfig);
			return;
		}
		if (string.IsNullOrWhiteSpace(ServerConfig.RemoteUploadAuthToken))
		{
			Logging.Log("Remote upload enabled, but RemoteUploadAuthToken is empty. Skipping upload.", ServerConfig);
			return;
		}
		lock (_remoteUploadSync)
		{
			if (_remoteUploadsCompleted.Contains(gameReferenceId))
			{
				Logging.Log($"Remote upload already completed for game reference {gameReferenceId}. Skipping duplicate export trigger.", ServerConfig);
				return;
			}
			if (_remoteUploadsInProgress.Contains(gameReferenceId))
			{
				Logging.Log($"Remote upload already in progress for game reference {gameReferenceId}. Skipping duplicate export trigger.", ServerConfig);
				return;
			}
			_remoteUploadsInProgress.Add(gameReferenceId);
		}
		Logging.Log($"Queueing remote stats upload for game reference {gameReferenceId}.", ServerConfig);
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				ExecuteRemoteGameUpload(statsJson, gameReferenceId, exportFileStem);
				lock (_remoteUploadSync)
				{
					_remoteUploadsInProgress.Remove(gameReferenceId);
					_remoteUploadsCompleted.Add(gameReferenceId);
				}
			}
			catch (RemoteUploadSkipException ex)
			{
				lock (_remoteUploadSync)
				{
					_remoteUploadsInProgress.Remove(gameReferenceId);
				}
				Logging.Log(ex.Message, ServerConfig);
			}
			catch (Exception arg)
			{
				lock (_remoteUploadSync)
				{
					_remoteUploadsInProgress.Remove(gameReferenceId);
				}
				Logging.LogError($"Remote stats upload failed for game reference {gameReferenceId}.\n{arg}", ServerConfig);
			}
		});
	}

	private static void ExecuteRemoteGameUpload(string statsJson, string gameReferenceId, string exportFileStem)
	{
		RemoteGameScheduleInfo orFetchRemoteGameScheduleInfo = GetOrFetchRemoteGameScheduleInfo(gameReferenceId);
		if (ServerConfig.WriteRemoteUploadMetadataFiles && !string.IsNullOrWhiteSpace(orFetchRemoteGameScheduleInfo.RawResponseJson))
		{
			TryWriteRemoteUploadMetadataFile(exportFileStem + "_nextgame.json", orFetchRemoteGameScheduleInfo.RawResponseJson);
		}
		Uri requestUri = CombineRemoteUploadUri(ServerConfig.RemoteUploadBaseUrl, "/api/games/" + Uri.EscapeDataString(orFetchRemoteGameScheduleInfo.GameId) + "/upload");
		Logging.Log($"Remote POST {requestUri}", ServerConfig);
		RemoteHttpResponse remoteHttpResponse = SendRemoteJsonRequest("POST", requestUri, ServerConfig.RemoteUploadAuthToken, statsJson, ServerConfig.RemoteUploadTimeoutSeconds);
		Logging.Log($"Remote POST {requestUri} -> {remoteHttpResponse.StatusCode}", ServerConfig);
		if (remoteHttpResponse.StatusCode < 200 || remoteHttpResponse.StatusCode >= 300)
		{
			throw new InvalidOperationException($"Upload endpoint returned HTTP {remoteHttpResponse.StatusCode}. Body: {TruncateForLog(remoteHttpResponse.Body)}");
		}
		if (ServerConfig.WriteRemoteUploadMetadataFiles && !string.IsNullOrWhiteSpace(remoteHttpResponse.Body))
		{
			TryWriteRemoteUploadMetadataFile(exportFileStem + "_upload_response.json", remoteHttpResponse.Body);
		}
		RemoteGameUploadResponse remoteGameUploadResponse = DeserializeRemoteJson<RemoteGameUploadResponse>(remoteHttpResponse.Body);
		string value = string.IsNullOrWhiteSpace(remoteGameUploadResponse?.GameId) ? orFetchRemoteGameScheduleInfo.GameId : remoteGameUploadResponse.GameId.Trim();
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append($"Remote stats upload succeeded for game {value}");
		if (remoteGameUploadResponse != null && remoteGameUploadResponse.HomeScore.HasValue && remoteGameUploadResponse.AwayScore.HasValue)
		{
			stringBuilder.Append($": {remoteGameUploadResponse.HomeScore.Value}-{remoteGameUploadResponse.AwayScore.Value}");
		}
		if (!string.IsNullOrWhiteSpace(remoteGameUploadResponse?.HomeTeamColor))
		{
			stringBuilder.Append($", homeTeamColor={remoteGameUploadResponse.HomeTeamColor.Trim()}");
		}
		if (remoteGameUploadResponse != null && remoteGameUploadResponse.TotalPlayers.HasValue)
		{
			stringBuilder.Append($", totalPlayers={remoteGameUploadResponse.TotalPlayers.Value}");
		}
		Logging.Log(stringBuilder.ToString(), ServerConfig);
	}

	private static RemoteGameScheduleInfo GetOrFetchRemoteGameScheduleInfo(string gameReferenceId)
	{
		lock (_remoteUploadSync)
		{
			if (_remoteSchedulesByGameReferenceId.TryGetValue(gameReferenceId, out var value))
			{
				return value;
			}
		}
		RemoteGameScheduleInfo remoteGameScheduleInfo = FetchRemoteGameScheduleInfo();
		lock (_remoteUploadSync)
		{
			if (_remoteSchedulesByGameReferenceId.TryGetValue(gameReferenceId, out var value2))
			{
				return value2;
			}
			_remoteSchedulesByGameReferenceId[gameReferenceId] = remoteGameScheduleInfo;
			return remoteGameScheduleInfo;
		}
	}

	private static RemoteGameScheduleInfo FetchRemoteGameScheduleInfo()
	{
		Uri requestUri = CombineRemoteUploadUri(ServerConfig.RemoteUploadBaseUrl, "/api/getNextGame");
		Logging.Log($"Remote GET {requestUri}", ServerConfig);
		RemoteHttpResponse remoteHttpResponse = SendRemoteJsonRequest("GET", requestUri, ServerConfig.RemoteUploadAuthToken, null, ServerConfig.RemoteUploadTimeoutSeconds);
		Logging.Log($"Remote GET {requestUri} -> {remoteHttpResponse.StatusCode}", ServerConfig);
		if (remoteHttpResponse.StatusCode == (int)HttpStatusCode.NotFound)
		{
			string apiError = ExtractApiError(remoteHttpResponse.Body);
			throw new RemoteUploadSkipException(string.IsNullOrWhiteSpace(apiError) ? "Remote upload skipped: No unplayed games remaining." : $"Remote upload skipped: {apiError}");
		}
		if (remoteHttpResponse.StatusCode < 200 || remoteHttpResponse.StatusCode >= 300)
		{
			throw new InvalidOperationException($"getNextGame returned HTTP {remoteHttpResponse.StatusCode}. Body: {TruncateForLog(remoteHttpResponse.Body)}");
		}
		RemoteGameScheduleResponse remoteGameScheduleResponse = DeserializeRemoteJson<RemoteGameScheduleResponse>(remoteHttpResponse.Body);
		if (remoteGameScheduleResponse == null)
		{
			throw new InvalidOperationException("getNextGame returned an empty response body.");
		}
		string text = remoteGameScheduleResponse.GameId?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException($"getNextGame did not return a gameId. Body: {TruncateForLog(remoteHttpResponse.Body)}");
		}
		RemoteGameScheduleInfo remoteGameScheduleInfo = new RemoteGameScheduleInfo
		{
			GameId = text,
			RawResponseJson = remoteHttpResponse.Body
		};
		string text2 = string.IsNullOrWhiteSpace(remoteGameScheduleResponse.Home) ? remoteGameScheduleResponse.HomeTeam?.Id ?? "" : remoteGameScheduleResponse.Home.Trim();
		string text3 = string.IsNullOrWhiteSpace(remoteGameScheduleResponse.Away) ? remoteGameScheduleResponse.AwayTeam?.Id ?? "" : remoteGameScheduleResponse.Away.Trim();
		string text4 = remoteGameScheduleResponse.HomeTeam?.Name?.Trim() ?? "";
		string text5 = remoteGameScheduleResponse.AwayTeam?.Name?.Trim() ?? "";
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append($"Resolved remote game {remoteGameScheduleInfo.GameId}");
		if (remoteGameScheduleResponse.Week.HasValue)
		{
			stringBuilder.Append($", week {remoteGameScheduleResponse.Week.Value}");
		}
		if (remoteGameScheduleResponse.GameIndex.HasValue)
		{
			stringBuilder.Append($", gameIndex {remoteGameScheduleResponse.GameIndex.Value}");
		}
		if (!string.IsNullOrWhiteSpace(text4) || !string.IsNullOrWhiteSpace(text5))
		{
			stringBuilder.Append($": {text4} vs {text5}");
		}
		if (!string.IsNullOrWhiteSpace(text2) || !string.IsNullOrWhiteSpace(text3))
		{
			stringBuilder.Append($" (home={text2}, away={text3})");
		}
		Logging.Log(stringBuilder.ToString(), ServerConfig);
		return remoteGameScheduleInfo;
	}

	private static RemoteHttpResponse SendRemoteJsonRequest(string method, Uri requestUri, string authToken, string requestBody, int timeoutSeconds)
	{
		if (requestUri == null || !requestUri.IsAbsoluteUri)
		{
			throw new InvalidOperationException("Remote upload request URI is null or not absolute.");
		}
		string host = requestUri.Host;
		int port = requestUri.IsDefaultPort ? (string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80) : requestUri.Port;
		int num = Math.Max(1, timeoutSeconds) * 1000;
		using TcpClient tcpClient = new TcpClient();
		tcpClient.ReceiveTimeout = num;
		tcpClient.SendTimeout = num;
		IAsyncResult asyncResult = tcpClient.BeginConnect(host, port, null, null);
		if (!asyncResult.AsyncWaitHandle.WaitOne(num))
		{
			throw new TimeoutException($"Timed out connecting to {host}:{port}.");
		}
		tcpClient.EndConnect(asyncResult);
		using Stream stream = CreateRemoteRequestStream(tcpClient, requestUri, num);
		byte[] bodyBytes = string.IsNullOrWhiteSpace(requestBody) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(requestBody);
		WriteRemoteHttpRequest(stream, method, requestUri, authToken, bodyBytes);
		return ReadRemoteHttpResponse(stream);
	}

	private static Stream CreateRemoteRequestStream(TcpClient tcpClient, Uri requestUri, int timeoutMilliseconds)
	{
		Stream stream = tcpClient.GetStream();
		stream.ReadTimeout = timeoutMilliseconds;
		stream.WriteTimeout = timeoutMilliseconds;
		if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return stream;
		}
		SslStream sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
		try
		{
			sslStream.AuthenticateAsClient(requestUri.Host, null, SslProtocols.Tls12, checkCertificateRevocation: false);
			return sslStream;
		}
		catch (Exception)
		{
			sslStream.Dispose();
			throw;
		}
	}

	private static void WriteRemoteHttpRequest(Stream stream, string method, Uri requestUri, string authToken, byte[] bodyBytes)
	{
		string text = string.IsNullOrEmpty(requestUri.PathAndQuery) ? "/" : requestUri.PathAndQuery;
		string text2 = requestUri.IsDefaultPort ? requestUri.Host : (requestUri.Host + ":" + requestUri.Port);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(method);
		stringBuilder.Append(' ');
		stringBuilder.Append(text);
		stringBuilder.Append(" HTTP/1.0\r\n");
		stringBuilder.Append("Host: ");
		stringBuilder.Append(text2);
		stringBuilder.Append("\r\n");
		stringBuilder.Append("User-Agent: DomaStatsBranch/1.0\r\n");
		stringBuilder.Append("Accept: application/json\r\n");
		stringBuilder.Append("Accept-Encoding: identity\r\n");
		stringBuilder.Append("Connection: close\r\n");
		if (!string.IsNullOrWhiteSpace(authToken))
		{
			stringBuilder.Append("X-Auth-Token: ");
			stringBuilder.Append(authToken.Trim());
			stringBuilder.Append("\r\n");
		}
		if (bodyBytes.Length > 0)
		{
			stringBuilder.Append("Content-Type: application/json; charset=utf-8\r\n");
			stringBuilder.Append("Content-Length: ");
			stringBuilder.Append(bodyBytes.Length);
			stringBuilder.Append("\r\n");
		}
		stringBuilder.Append("\r\n");
		byte[] bytes = Encoding.ASCII.GetBytes(stringBuilder.ToString());
		stream.Write(bytes, 0, bytes.Length);
		if (bodyBytes.Length > 0)
		{
			stream.Write(bodyBytes, 0, bodyBytes.Length);
		}
		stream.Flush();
	}

	private static RemoteHttpResponse ReadRemoteHttpResponse(Stream stream)
	{
		using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);
		string text = streamReader.ReadLine();
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("Remote endpoint returned an empty HTTP response.");
		}
		string[] array = text.Split(new char[1] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length < 2 || !int.TryParse(array[1], out var result))
		{
			throw new InvalidOperationException($"Remote endpoint returned an invalid HTTP status line: {text}");
		}
		string text2;
		while (!string.IsNullOrEmpty(text2 = streamReader.ReadLine()))
		{
		}
		return new RemoteHttpResponse
		{
			StatusCode = result,
			StatusText = ((array.Length >= 3) ? array[2] : ""),
			Body = streamReader.ReadToEnd()
		};
	}

	private static T DeserializeRemoteJson<T>(string rawJson) where T : class
	{
		if (string.IsNullOrWhiteSpace(rawJson))
		{
			return null;
		}
		try
		{
			return JsonConvert.DeserializeObject<T>(rawJson);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"Remote endpoint returned invalid JSON. Body: {TruncateForLog(rawJson)}", ex);
		}
	}

	private static void TryWriteRemoteUploadMetadataFile(string path, string contents)
	{
		if (string.IsNullOrWhiteSpace(path) || contents == null)
		{
			return;
		}
		try
		{
			string directoryName = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(path, contents, Encoding.UTF8);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't write remote upload metadata file {path}.\n{arg}", ServerConfig);
		}
	}

	private static Uri CombineRemoteUploadUri(string baseUrl, string relativePath)
	{
		string text = NormalizeRemoteUploadValue(baseUrl);
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("RemoteUploadBaseUrl is empty after normalization.");
		}
		if (!text.EndsWith("/", StringComparison.Ordinal))
		{
			text += "/";
		}
		if (!Uri.TryCreate(text, UriKind.Absolute, out var result))
		{
			throw new InvalidOperationException($"RemoteUploadBaseUrl is invalid after normalization: '{text}'");
		}
		if (!string.Equals(result.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(result.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"RemoteUploadBaseUrl must use http or https. Value after normalization: '{text}'");
		}
		string text2 = NormalizeRemoteUploadValue(relativePath).TrimStart('/');
		if (string.IsNullOrWhiteSpace(text2))
		{
			return result;
		}
		return new Uri(result, text2);
	}

	private static string NormalizeRemoteUploadValue(string value)
	{
		string text = RemoveRemoteUploadInvisibleCharacters(value);
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		return text.Trim().Trim('"', '\'');
	}

	private static string RemoveRemoteUploadInvisibleCharacters(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? "";
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length);
		foreach (char c in value)
		{
			switch (c)
			{
			case '\u200B':
			case '\u200C':
			case '\u200D':
			case '\u2060':
			case '\uFEFF':
				continue;
			default:
				stringBuilder.Append(c);
				break;
			}
		}
		return stringBuilder.ToString();
	}

	private static string ExtractApiError(string responseBody)
	{
		if (string.IsNullOrWhiteSpace(responseBody))
		{
			return "";
		}
		try
		{
			return JObject.Parse(responseBody).Value<string>("error") ?? "";
		}
		catch
		{
			return responseBody.Trim();
		}
	}

	private static string TruncateForLog(string value, int maxLength = 600)
	{
		if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
		{
			return value ?? "";
		}
		return value.Substring(0, maxLength) + "...";
	}
}

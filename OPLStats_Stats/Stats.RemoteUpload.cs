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
	private sealed class RemoteHttpResponse
	{
		public int StatusCode { get; set; }

		public string StatusText { get; set; } = "";

		public string Body { get; set; } = "";
	}

	private sealed class RemoteTeamResponse
	{
		public string Id { get; set; } = "";

		public string Name { get; set; } = "";
	}

	private sealed class RemoteLeagueGameResponse
	{
		public int? Division { get; set; }

		public int? Week { get; set; }

		public string Round { get; set; } = "";

		public int? GameIndex { get; set; }

		public RemoteTeamResponse HomeTeam { get; set; }

		public RemoteTeamResponse AwayTeam { get; set; }
	}

	private sealed class RemoteGameUploadResponse
	{
		public string UploadId { get; set; } = "";

		public string Category { get; set; } = "";

		public string GameId { get; set; } = "";

		public bool Duplicate { get; set; }

		public int? HomeScore { get; set; }

		public int? AwayScore { get; set; }

		public string HomeTeamColor { get; set; } = "";

		public int? TotalPlayers { get; set; }

		public string Note { get; set; } = "";

		public string Warning { get; set; } = "";

		public string Error { get; set; } = "";

		public RemoteLeagueGameResponse League { get; set; }
	}

	// Waits before each retry of a transient failure (network error, HTTP 429 or 5xx). The server dedupes
	// uploads by server + game reference, so re-sending after an ambiguous failure cannot create a second game.
	private static readonly int[] _remoteUploadRetryDelaysSeconds = new int[2] { 5, 20 };

	private static readonly string[] _remoteUploadGameTypes = new string[4] { "auto", "league", "pugs", "unassigned" };

	private static readonly object _remoteUploadSync = new object();

	private static readonly HashSet<string> _remoteUploadsInProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> _remoteUploadsCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
		string gameType = NormalizeRemoteUploadValue(ServerConfig.RemoteUploadGameType).ToLowerInvariant();
		if (Array.IndexOf(_remoteUploadGameTypes, gameType) < 0)
		{
			Logging.Log($"RemoteUploadGameType '{ServerConfig.RemoteUploadGameType}' is not one of {string.Join(", ", _remoteUploadGameTypes)}. Using 'auto'.", ServerConfig);
			gameType = "auto";
		}
		Uri requestUri = CombineRemoteUploadUri(ServerConfig.RemoteUploadBaseUrl, "/api/server/games?reference=" + Uri.EscapeDataString(gameReferenceId) + "&type=" + Uri.EscapeDataString(gameType));
		RemoteHttpResponse remoteHttpResponse = SendRemoteUploadWithRetries(requestUri, statsJson);
		if (remoteHttpResponse.StatusCode == (int)HttpStatusCode.Unauthorized || remoteHttpResponse.StatusCode == (int)HttpStatusCode.Forbidden)
		{
			throw new InvalidOperationException($"Upload was rejected (HTTP {remoteHttpResponse.StatusCode}): {ExtractApiError(remoteHttpResponse.Body)}. Check that RemoteUploadAuthToken is an active server API key from the website's Servers page.");
		}
		if (remoteHttpResponse.StatusCode < 200 || remoteHttpResponse.StatusCode >= 300)
		{
			throw new InvalidOperationException($"Upload endpoint returned HTTP {remoteHttpResponse.StatusCode}. Body: {TruncateForLog(remoteHttpResponse.Body)}");
		}
		if (ServerConfig.WriteRemoteUploadMetadataFiles && !string.IsNullOrWhiteSpace(remoteHttpResponse.Body))
		{
			TryWriteRemoteUploadMetadataFile(exportFileStem + "_upload_response.json", remoteHttpResponse.Body);
		}
		Logging.Log(DescribeRemoteUploadResult(gameReferenceId, DeserializeRemoteJson<RemoteGameUploadResponse>(remoteHttpResponse.Body)), ServerConfig);
	}

	private static RemoteHttpResponse SendRemoteUploadWithRetries(Uri requestUri, string statsJson)
	{
		for (int attempt = 0; ; attempt++)
		{
			bool canRetry = attempt < _remoteUploadRetryDelaysSeconds.Length;
			string failure;
			try
			{
				Logging.Log($"Remote POST {requestUri}{(attempt > 0 ? $" (retry {attempt})" : "")}", ServerConfig);
				RemoteHttpResponse response = SendRemoteJsonRequest("POST", requestUri, ServerConfig.RemoteUploadAuthToken, statsJson, ServerConfig.RemoteUploadTimeoutSeconds);
				Logging.Log($"Remote POST {requestUri} -> {response.StatusCode}", ServerConfig);
				if (!canRetry || (response.StatusCode != 429 && response.StatusCode < 500))
				{
					return response;
				}
				failure = $"HTTP {response.StatusCode}";
			}
			catch (Exception ex) when (canRetry && (ex is IOException || ex is SocketException || ex is TimeoutException || ex is AuthenticationException))
			{
				failure = ex.GetType().Name + ": " + ex.Message;
			}
			int delaySeconds = _remoteUploadRetryDelaysSeconds[attempt];
			Logging.Log($"Remote upload attempt {attempt + 1} failed ({failure}). Retrying in {delaySeconds}s.", ServerConfig);
			Thread.Sleep(delaySeconds * 1000);
		}
	}

	private static string DescribeRemoteUploadResult(string gameReferenceId, RemoteGameUploadResponse response)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append($"Remote stats upload succeeded for {gameReferenceId}");
		if (response == null)
		{
			return stringBuilder.ToString();
		}
		string category = string.IsNullOrWhiteSpace(response.Category) ? "unknown" : response.Category.Trim();
		stringBuilder.Append(category == "unassigned" ? " -> held for review on the website" : $" -> {category}");
		RemoteLeagueGameResponse league = response.League;
		if (league != null)
		{
			stringBuilder.Append(league.Division.HasValue ? $" D{league.Division.Value}" : "");
			stringBuilder.Append(!string.IsNullOrWhiteSpace(league.Round) ? $" {league.Round}" : (league.Week.HasValue ? $" week {league.Week.Value}" : ""));
			stringBuilder.Append(league.GameIndex.HasValue ? $" game {league.GameIndex.Value + 1}" : "");
			stringBuilder.Append($": {league.HomeTeam?.Name ?? "?"} vs {league.AwayTeam?.Name ?? "?"}");
		}
		if (response.HomeScore.HasValue && response.AwayScore.HasValue)
		{
			stringBuilder.Append($", {response.HomeScore.Value}-{response.AwayScore.Value}");
		}
		if (!string.IsNullOrWhiteSpace(response.HomeTeamColor))
		{
			stringBuilder.Append($", homeTeamColor={response.HomeTeamColor.Trim()}");
		}
		if (response.TotalPlayers.HasValue)
		{
			stringBuilder.Append($", totalPlayers={response.TotalPlayers.Value}");
		}
		if (response.Duplicate)
		{
			stringBuilder.Append(" (re-sent; existing game updated)");
		}
		string note = string.IsNullOrWhiteSpace(response.Warning) ? response.Note : response.Warning;
		if (!string.IsNullOrWhiteSpace(note))
		{
			stringBuilder.Append($". Note: {note.Trim()}");
		}
		return stringBuilder.ToString();
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
			stringBuilder.Append("Authorization: Bearer ");
			stringBuilder.Append(NormalizeRemoteUploadValue(authToken));
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

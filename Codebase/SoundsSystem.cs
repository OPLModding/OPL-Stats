using System;

namespace Codebase;

internal class SoundsSystem
{
	internal const string LOAD_SOUNDS = "loadsounds";

	internal const string PLAY_SOUND = "playsound";

	internal const string STOP_SOUND = "stopsound";

	internal const string LOAD_EXTRA_SOUNDS = "loadextrasounds";

	internal const string ALL = "all";

	internal const string MUSIC = "music";

	internal const string WHISTLE = "whistle";

	internal const string BLUEGOALHORN = "bluegoalhorn";

	internal const string REDGOALHORN = "redgoalhorn";

	internal const string FACEOFF_MUSIC = "faceoffmusic";

	internal const string FACEOFF_MUSIC_DELAYED = "faceoffmusicd";

	internal const string BLUE_GOAL_MUSIC = "bluegoalmusic";

	internal const string RED_GOAL_MUSIC = "redgoalmusic";

	internal const string BETWEEN_PERIODS_MUSIC = "betweenperiodsmusic";

	internal const string WARMUP_MUSIC = "warmupmusic";

	internal const string LAST_MINUTE_MUSIC = "lastminutemusic";

	internal const string LAST_MINUTE_MUSIC_DELAYED = "lastminutemusicd";

	internal const string FIRST_FACEOFF_MUSIC = "faceofffirstmusic";

	internal const string FIRST_FACEOFF_MUSIC_DELAYED = "faceofffirstmusicd";

	internal const string SECOND_FACEOFF_MUSIC = "faceoffsecondmusic";

	internal const string SECOND_FACEOFF_MUSIC_DELAYED = "faceoffsecondmusicd";

	internal const string GAMEOVER_MUSIC = "gameovermusic";

	internal static string FormatSoundStrForCommunication(string sound)
	{
		return sound + $";{new Random().Next(0, 100000)}";
	}
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "9090"}");

var app = builder.Build();

var sessions = new Dictionary<string, Dictionary<string, PlayerData>>();
var chatMessages = new Dictionary<string, List<ChatMessage>>();
const int SessionTimeout = 30;
const int PlayerVisibleTime = 15;
const int MaxChatAge = 300;
const int CleanupInterval = 10;

_ = Task.Run(async () =>
{
    while (true)
    {
        Cleanup();
        await Task.Delay(TimeSpan.FromSeconds(CleanupInterval));
    }
});

_ = Task.Run(async () =>
{
    var extUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
    if (!string.IsNullOrEmpty(extUrl))
    {
        using var http = new HttpClient();
        while (true)
        {
            try { await http.GetAsync(extUrl); }
            catch { }
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }
});

app.MapGet("/", () => Results.Ok(new
{
    status = "ok",
    players = sessions.Sum(kv => kv.Value.Count)
}));

var HandleApi = (HttpRequest req) =>
{
    var gameId = req.Query["game_id"].FirstOrDefault();
    if (string.IsNullOrEmpty(gameId))
        return Results.Ok(new { error = "invalid game_id" });

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var username = req.Query["username"].FirstOrDefault();
    var exclude = req.Query["exclude"].FirstOrDefault() ?? "";

    if (!sessions.ContainsKey(gameId))
        sessions[gameId] = new Dictionary<string, PlayerData>();

    var game = sessions[gameId];

    if (!string.IsNullOrEmpty(username) && req.Query.ContainsKey("pos_x"))
    {
        game[username] = new PlayerData
        {
            PosX = ParseFloat(req.Query["pos_x"]),
            PosY = ParseFloat(req.Query["pos_y"]),
            PosZ = ParseFloat(req.Query["pos_z"]),
            Rotation = ParseFloat(req.Query["rotation"]),
            HeadR = ParseInt(req.Query["head_r"], 243),
            HeadG = ParseInt(req.Query["head_g"], 201),
            HeadB = ParseInt(req.Query["head_b"], 74),
            TorsoR = ParseInt(req.Query["torso_r"], 163),
            TorsoG = ParseInt(req.Query["torso_g"], 162),
            TorsoB = ParseInt(req.Query["torso_b"], 165),
            LegR = ParseInt(req.Query["leg_r"], 162),
            LegG = ParseInt(req.Query["leg_g"], 205),
            LegB = ParseInt(req.Query["leg_b"], 53),
            WearingHat = ParseInt(req.Query["wearing_hat"], 0),
            IsAdmin = ParseInt(req.Query["is_admin"], 0),
            HatColor = req.Query["hat_color"].FirstOrDefault() ?? "",
            WearingHair = ParseInt(req.Query["wearing_hair"], 0),
            Time = now
        };
    }

    if (!string.IsNullOrEmpty(username) && req.Query.ContainsKey("chat_msg"))
    {
        if (!chatMessages.ContainsKey(gameId))
            chatMessages[gameId] = new List<ChatMessage>();

        chatMessages[gameId].Add(new ChatMessage
        {
            Id = chatMessages[gameId].Count + 1,
            Username = username,
            Message = (req.Query["chat_msg"].FirstOrDefault() ?? "").Length > 200
                ? (req.Query["chat_msg"].FirstOrDefault() ?? "")[..200]
                : (req.Query["chat_msg"].FirstOrDefault() ?? ""),
            Time = now
        });
    }

    var players = new List<object>();
    foreach (var (k, v) in game)
    {
        if (k != exclude && now - v.Time < PlayerVisibleTime)
        {
            players.Add(new
            {
                username = k,
                pos_x = v.PosX,
                pos_y = v.PosY,
                pos_z = v.PosZ,
                rotation = v.Rotation,
                head_r = v.HeadR,
                head_g = v.HeadG,
                head_b = v.HeadB,
                torso_r = v.TorsoR,
                torso_g = v.TorsoG,
                torso_b = v.TorsoB,
                leg_r = v.LegR,
                leg_g = v.LegG,
                leg_b = v.LegB,
                wearing_hat = v.WearingHat,
                is_admin = v.IsAdmin,
                hat_color = v.HatColor,
                wearing_hair = v.WearingHair
            });
        }
    }

    var lastChatId = ParseInt(req.Query["last_chat_id"], 0);
    var chat = new List<object>();
    if (chatMessages.TryGetValue(gameId, out var msgs))
    {
        foreach (var m in msgs)
        {
            if (m.Id > lastChatId)
                chat.Add(new { id = m.Id, username = m.Username, message = m.Message });
        }
    }

    return Results.Ok(new { players, chat });
};

app.MapGet("/api", HandleApi);
app.MapGet("/api/games/multiplayer.php", HandleApi);

app.Run();

static float ParseFloat(string value)
{
    float.TryParse(value, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var result);
    return result;
}

static int ParseInt(string value, int defaultValue = 0)
{
    int.TryParse(value, out var result);
    return result == 0 ? defaultValue : result;
}

void Cleanup()
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var gameIdsToRemove = new List<string>();

    foreach (var (gameId, game) in sessions)
    {
        var usersToRemove = new List<string>();
        foreach (var (user, data) in game)
        {
            if (now - data.Time > SessionTimeout)
                usersToRemove.Add(user);
        }
        foreach (var u in usersToRemove)
            game.Remove(u);

        if (game.Count == 0)
            gameIdsToRemove.Add(gameId);
    }
    foreach (var id in gameIdsToRemove)
        sessions.Remove(id);

    if (chatMessages.Count > 0)
    {
        var chatIdsToRemove = new List<string>();
        foreach (var (gameId, msgs) in chatMessages)
        {
            msgs.RemoveAll(m => now - m.Time > MaxChatAge);
            if (msgs.Count == 0)
                chatIdsToRemove.Add(gameId);
        }
        foreach (var id in chatIdsToRemove)
            chatMessages.Remove(id);
    }
}

record PlayerData
{
    public float PosX, PosY, PosZ, Rotation;
    public int HeadR, HeadG, HeadB, TorsoR, TorsoG, TorsoB, LegR, LegG, LegB;
    public int WearingHat, IsAdmin, WearingHair;
    public string HatColor = "";
    public long Time;
}

record ChatMessage
{
    public int Id;
    public string Username = "";
    public string Message = "";
    public long Time;
}

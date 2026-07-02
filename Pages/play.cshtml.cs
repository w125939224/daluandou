using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace daluandou.Pages
{
    [Authorize]
    public class PlayModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<GameRoomHub> _hubContext;
        private static readonly Random _random = new();
        private static readonly string[] AllColors = { "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD", "#F39C12", "#8E44AD" };

        public PlayModel(AppDbContext context, IHubContext<GameRoomHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        [BindProperty]
        public CreateRoomInputModel CreateRoomModel { get; set; } = new CreateRoomInputModel();

        [BindProperty]
        public JoinRoomInputModel JoinRoomModel { get; set; } = new JoinRoomInputModel();

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostQuickMatch()
        {
            try
            {
                var availableRoom = await _context.GameRooms
                    .FirstOrDefaultAsync(r => r.IsPublic && r.RoomStatus == "Waiting" && r.CurrentPlayers < r.MaxPlayers);
                if (availableRoom != null)
                    return await JoinRoomInternal(availableRoom.Id, false);
                else
                {
                    var newRoom = new GameRooms
                    {
                        RoomOwner = User.Identity.Name,
                        IsGameOver = false,
                        Players = null,
                        RoomCode = GenerateRoomCode(),
                        MaxCount = 100,
                        IsPublic = true,
                        RoomStatus = "Waiting",
                        MaxPlayers = 4,
                        CurrentPlayers = 0,
                        CreatedTime = DateTime.UtcNow
                    };
                    _context.GameRooms.Add(newRoom);
                    await _context.SaveChangesAsync();
                    return await JoinRoomInternal(newRoom.Id, false);
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"匹配失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        public async Task<IActionResult> OnPostCreateRoom()
        {
            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "请检查输入信息";
                return RedirectToPage();
            }

            try
            {
                var newRoom = new GameRooms
                {
                    RoomOwner = User.Identity.Name,
                    IsGameOver = false,
                    Players = null,
                    RoomCode = GenerateRoomCode(),
                    MaxCount = CreateRoomModel.MaxCount,
                    IsPublic = CreateRoomModel.IsPublic,
                    RoomStatus = "Waiting",
                    MaxPlayers = CreateRoomModel.MaxPlayers,
                    CurrentPlayers = 0,
                    CreatedTime = DateTime.UtcNow
                };
                _context.GameRooms.Add(newRoom);
                await _context.SaveChangesAsync();

                // 加入房主
                var result = await JoinRoomInternal(newRoom.Id, false);
                if (result is not RedirectToPageResult) return result;

                int botsToAdd = Math.Min(CreateRoomModel.BotCount, newRoom.MaxPlayers - 1);
                for (int i = 0; i < botsToAdd; i++)
                {
                    await AddBotToRoom(newRoom.Id, i + 1);
                }

                return RedirectToPage("GameRoom", new { roomId = newRoom.Id, playerId = GetCurrentPlayerId(newRoom.Id) });
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"创建房间失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        public async Task<IActionResult> OnPostJoinRoom()
        {
            if (!ModelState.IsValid || string.IsNullOrWhiteSpace(JoinRoomModel.RoomCode))
            {
                TempData["ErrorMessage"] = "请输入有效的房间号";
                return RedirectToPage();
            }

            try
            {
                var roomCode = JoinRoomModel.RoomCode.Trim().ToUpper();
                var room = await _context.GameRooms.FirstOrDefaultAsync(r => r.RoomCode == roomCode && r.RoomStatus == "Waiting");
                if (room == null)
                {
                    TempData["ErrorMessage"] = "房间不存在或游戏已开始";
                    return RedirectToPage();
                }
                if (room.CurrentPlayers >= room.MaxPlayers)
                {
                    TempData["ErrorMessage"] = "房间已满";
                    return RedirectToPage();
                }
                return await JoinRoomInternal(room.Id, false);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"加入房间失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        private async Task<IActionResult> JoinRoomInternal(int roomId, bool isBot)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var username = User.Identity.Name;
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null) { TempData["ErrorMessage"] = "房间不存在"; return RedirectToPage(); }
            if (room.CurrentPlayers >= room.MaxPlayers) { TempData["ErrorMessage"] = "房间已满"; return RedirectToPage(); }

            var existingPlayer = await _context.GamePlayers
                .FirstOrDefaultAsync(p => p.UserId == userId && p.GameRoomId == roomId && !p.IsBot);
            if (existingPlayer != null)
            {
                return room.RoomStatus == "Waiting"
                    ? RedirectToPage("GameRoom", new { roomId, playerId = existingPlayer.Id })
                    : RedirectToPage("Game", new { roomId, playerId = existingPlayer.Id });
            }

            var playerColor = await GetAvailableColor(roomId);
            var gamePlayer = new GamePlayer
            {
                UserId = isBot ? null : userId,
                PlayerName = username ?? "机器人",
                PlayerColor = playerColor,
                GameRoomId = room.Id,
                GameRoom = room.RoomCode,
                CurrentPosition = 1,
                Gold = 0,
                DC = 0,
                AC = 0,
                HP = 100,
                MP = 100,
                HPMAX = 100,
                MPMAX = 100,
                Weapon = "无",
                Dress = "无",
                Helmet = "无",
                Necklace = "无",
                Ring = "无",
                Armring = "无",
                IsBot = isBot
            };

            _context.GamePlayers.Add(gamePlayer);
            room.CurrentPlayers++;
            if (room.CurrentPlayers == 1)
                room.CurrentTurnPlayerId = gamePlayer.Id;

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGameRoom", roomId);
            return RedirectToPage("GameRoom", new { roomId, playerId = gamePlayer.Id });
        }

        private async Task AddBotToRoom(int roomId, int botIndex)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.CurrentPlayers >= room.MaxPlayers) return;

            var color = await GetAvailableColor(roomId);
            var bot = new GamePlayer
            {
                UserId = null,
                PlayerName = $"机器人{botIndex}",
                PlayerColor = color,
                GameRoomId = roomId,
                GameRoom = room.RoomCode,
                CurrentPosition = 1,
                Gold = 0,
                HP = 100,
                MP = 100,
                HPMAX = 100,
                MPMAX = 100,
                Weapon = "无",
                Dress = "无",
                Helmet = "无",
                Necklace = "无",
                Ring = "无",
                Armring = "无",
                IsBot = true
            };
            _context.GamePlayers.Add(bot);
            room.CurrentPlayers++;
            if (room.CurrentPlayers == 1) room.CurrentTurnPlayerId = bot.Id;
            await _context.SaveChangesAsync();
        }

        private async Task<string> GetAvailableColor(int roomId)
        {
            var usedColors = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .Select(p => p.PlayerColor)
                .ToListAsync();
            return AllColors.FirstOrDefault(c => !usedColors.Contains(c)) ?? AllColors[_random.Next(AllColors.Length)];
        }

        private int GetCurrentPlayerId(int roomId)
        {
            return _context.GamePlayers
                .Where(p => p.GameRoomId == roomId && p.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier))
                .Select(p => p.Id)
                .FirstOrDefault();
        }

        private string GenerateRoomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, 6).Select(s => s[_random.Next(s.Length)]).ToArray());
        }
    }

    public class CreateRoomInputModel
    {
        public int MaxPlayers { get; set; } = 4;
        public bool IsPublic { get; set; } = true;
        public int MaxCount { get; set; } = 100;
        public int BotCount { get; set; } = 0;
    }

    public class JoinRoomInputModel
    {
        public string RoomCode { get; set; } = string.Empty;
    }
}
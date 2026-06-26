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
        private static readonly Random _random = new Random();

        // 唯一的构造函数（注入两个依赖）
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

        // 快速匹配处理
        public async Task<IActionResult> OnPostQuickMatch()
        {
            try
            {
                var availableRoom = await _context.GameRooms
                    .FirstOrDefaultAsync(r => r.IsPublic &&
                                             r.RoomStatus == "Waiting" &&
                                             r.CurrentPlayers < r.MaxPlayers);

                if (availableRoom != null)
                {
                    return await JoinRoomInternal(availableRoom.Id);
                }
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
                    await _context.Database.ExecuteSqlRawAsync("SET NAMES utf8mb4 COLLATE utf8mb4_unicode_ci");
                    await _context.SaveChangesAsync();

                    return await JoinRoomInternal(newRoom.Id);
                }
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"匹配失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        // 创建房间处理
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

                return await JoinRoomInternal(newRoom.Id);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"创建房间失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        // 加入房间处理
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

                var room = await _context.GameRooms
                    .FirstOrDefaultAsync(r => r.RoomCode == roomCode && r.RoomStatus == "Waiting");

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

                return await JoinRoomInternal(room.Id);
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"加入房间失败：{ex.Message}";
                return RedirectToPage();
            }
        }

        private async Task<IActionResult> JoinRoomInternal(int roomId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var username = User.Identity.Name;

            var room = await _context.GameRooms.FindAsync(roomId);

            if (room == null)
            {
                TempData["ErrorMessage"] = "房间不存在";
                return RedirectToPage();
            }

            if (room.CurrentPlayers >= room.MaxPlayers)
            {
                TempData["ErrorMessage"] = "房间已满";
                return RedirectToPage();
            }

            // 防止同一个用户多次加入同一个房间
            var existingPlayer = await _context.GamePlayers
                .FirstOrDefaultAsync(p => p.UserId == userId && p.GameRoomId == roomId);

            if (existingPlayer != null)
            {
                return RedirectToPage("GameRoom", new { roomId = roomId, playerId = existingPlayer.Id });
            }

            var isOwner = room.CurrentPlayers == 0;
            var playerColor = GetRandomColor();
            var gamePlayer = new GamePlayer
            {
                UserId = userId,
                PlayerName = username,
                PlayerColor = playerColor,
                GameRoomId = room.Id,
                GameRoom = room.RoomCode,
                CurrentPosition = 0,
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
                Armring = "无"
            };

            _context.GamePlayers.Add(gamePlayer);
            room.CurrentPlayers++;

            if (isOwner)
            {
                room.CurrentTurnPlayerId = gamePlayer.Id;
            }

            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGameRoom", roomId);

            return RedirectToPage("GameRoom", new { roomId = roomId, playerId = gamePlayer.Id });
        }

        private string GetRandomColor()
        {
            string[] colors = { "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD" };
            return colors[_random.Next(colors.Length)];
        }

        private string GenerateRoomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[_random.Next(s.Length)]).ToArray());
        }
    }

    // 创建房间输入模型
    public class CreateRoomInputModel
    {
        public int MaxPlayers { get; set; } = 4;
        public bool IsPublic { get; set; } = true;
        public int MaxCount { get; set; } = 100;
    }

    // 加入房间输入模型
    public class JoinRoomInputModel
    {
        public string RoomCode { get; set; } = string.Empty;
    }
}
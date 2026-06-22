using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace daluandou.Pages
{
    [Authorize]
    public class PlayModel : PageModel
    {
        private readonly AppDbContext _context;

        private static readonly Random _random = new Random();

        public PlayModel(AppDbContext context)
        {
            _context = context;
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
                // 查找等待中的公开房间
                var availableRoom = await _context.GameRooms
                    .FirstOrDefaultAsync(r => r.IsPublic &&
                                             r.RoomStatus == "Waiting" &&
                                             r.CurrentPlayers < r.MaxPlayers);

                if (availableRoom != null)
                {
                    // 加入找到的房间
                    return await JoinRoomInternal(availableRoom.Id);
                }
                else
                {
                    // 没有可用房间，创建一个新的公开房间
                    var newRoom = new GameRooms
                    {
                        // 快速匹配创建的房间房主也设为当前登录用户
                        RoomOwner = User.Identity.Name,
                        IsGameOver = false,
                        Players = null,
                        RoomCode = GenerateRoomCode(),
                        MaxCount = 100,
                        IsPublic = true,
                        RoomStatus = "Waiting",
                        MaxPlayers = 4,
                        CurrentPlayers = 0,
                        // 使用UTC时间避免时区问题
                        CreatedTime = DateTime.UtcNow
                    };

                    _context.GameRooms.Add(newRoom);
                    await _context.SaveChangesAsync();

                    // 加入新创建的房间
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
                    // 因为有[Authorize]保护，User.Identity.Name一定不为null
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

                // 加入自己创建的房间
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
            // 获取当前登录用户的ID和用户名
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
                // 如果用户已经在房间里，直接重定向到游戏页面
                return RedirectToPage("Game", new { roomId = roomId, playerId = existingPlayer.Id });
            }

            var isOwner = room.CurrentPlayers == 0;
            var gamePlayer = new GamePlayer
            {
                // 使用真实的用户ID和用户名，而不是随机生成
                UserId = userId,
                UserName = username,
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

            // 房主自动成为第一回合玩家
            if (isOwner)
            {
                room.CurrentTurnPlayerId = gamePlayer.Id;
            }

            await _context.SaveChangesAsync();

            // 重定向到游戏页面
            return RedirectToPage("Game", new { roomId = roomId, playerId = gamePlayer.Id });
        }

        // 生成6位随机房间号（修复Random重复问题）
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
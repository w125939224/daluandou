using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace daluandou.Pages
{
    public class GameRoomHub : Hub
    {
        public async Task JoinRoomGroup(int roomId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"game_{roomId}");
        }

        public async Task LeaveGameGroup(int roomId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"game_{roomId}");
        }
    }

    [Authorize]
    public class GameRoomModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<GameRoomHub> _hubContext;
        private static readonly string[] AllColors = { "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7", "#DDA0DD", "#F39C12", "#8E44AD" };

        public GameRoomModel(AppDbContext context, IHubContext<GameRoomHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        public GameRooms Room { get; set; }
        public List<GamePlayer> Players { get; set; } = new();
        public int CurrentPlayerId { get; set; }

        public async Task<IActionResult> OnGetAsync(int roomId, int playerId)
        {
            Room = await _context.GameRooms.FindAsync(roomId);
            if (Room == null) return NotFound();

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return Forbid();

            CurrentPlayerId = playerId;
            Players = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .ToListAsync();

            if (Room.RoomStatus == "Playing")
                return RedirectToPage("Game", new { roomId, playerId });

            return Page();
        }

        public async Task<IActionResult> OnPostStartGameAsync(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null) return NotFound();
            if (room.RoomOwner != User.Identity?.Name) return Forbid();
            if (room.CurrentPlayers < 2)
            {
                TempData["ErrorMessage"] = "至少需要2名玩家才能开始游戏";
                return RedirectToPage(new { roomId, playerId });
            }

            room.RoomStatus = "Playing";
            room.GameStartTime = DateTime.UtcNow;
            var playersInRoom = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .ToListAsync();
            if (playersInRoom.Any())
                room.CurrentTurnPlayerId = playersInRoom.First().Id;

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("StartGame", roomId, playerId);
            return RedirectToPage("Game", new { roomId, playerId });
        }

        // 离开房间（等待阶段）
        public async Task<IActionResult> OnPostLeaveRoomAsync(int roomId, int playerId)
        {
            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return RedirectToPage("Play");

            var room = await _context.GameRooms.FindAsync(roomId);
            if (room != null)
            {
                _context.GamePlayers.Remove(player);
                room.CurrentPlayers--;

                if (room.RoomOwner == User.Identity?.Name && room.CurrentPlayers > 0)
                {
                    var nextOwner = await _context.GamePlayers
                        .Where(p => p.GameRoomId == roomId && p.Id != playerId)
                        .FirstOrDefaultAsync();
                    if (nextOwner != null)
                        room.RoomOwner = nextOwner.PlayerName;
                }
                await _context.SaveChangesAsync();
                await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGameRoom", roomId);
            }
            return RedirectToPage("Play");
        }

        public async Task<IActionResult> OnPostAddBotAsync(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomOwner != User.Identity?.Name || room.CurrentPlayers >= room.MaxPlayers)
                return Forbid();

            var usedBots = await _context.GamePlayers.CountAsync(p => p.GameRoomId == roomId && p.IsBot);
            var color = await GetAvailableColor(roomId);
            var bot = new GamePlayer
            {
                PlayerName = $"机器人{usedBots + 1}",
                PlayerColor = color,
                GameRoomId = roomId,
                GameRoom = room.RoomCode,
                CurrentPosition = 1,
                Gold = 500,
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
                IsBot = true
            };
            _context.GamePlayers.Add(bot);
            room.CurrentPlayers++;
            if (room.CurrentPlayers == 1) room.CurrentTurnPlayerId = bot.Id;

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGameRoom", roomId);
            return RedirectToPage(new { roomId, playerId });
        }

        private async Task<string> GetAvailableColor(int roomId)
        {
            var usedColors = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .Select(p => p.PlayerColor)
                .ToListAsync();
            return AllColors.FirstOrDefault(c => !usedColors.Contains(c)) ?? AllColors[0];
        }
    }
}
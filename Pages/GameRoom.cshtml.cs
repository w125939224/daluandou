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
        // 统一使用 JoinRoomGroup（首字母大写）
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

            // 通知所有玩家游戏开始（事件名保持大写开头）
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("StartGame", roomId, playerId);

            return RedirectToPage("Game", new { roomId, playerId });
        }
    }
}
using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace daluandou.Pages
{
    [Authorize]
    public class GameModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly IHubContext<GameRoomHub> _hubContext;

        public GameModel(AppDbContext context, IHubContext<GameRoomHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        public GameRooms Room { get; set; }
        public List<GamePlayer> Players { get; set; } = new();
        public GamePlayer CurrentPlayer { get; set; }
        public bool IsMyTurn { get; set; }

        public async Task<IActionResult> OnGetAsync(int roomId, int playerId)
        {
            Room = await _context.GameRooms.FindAsync(roomId);
            if (Room == null || Room.RoomStatus != "Playing")
                return NotFound();

            Players = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .OrderBy(p => p.Id)
                .ToListAsync();

            CurrentPlayer = Players.FirstOrDefault(p => p.Id == playerId);
            if (CurrentPlayer == null)
                return Forbid();

            IsMyTurn = (Room.CurrentTurnPlayerId == playerId) && !CurrentPlayer.IsBot;
            return Page();
        }

        // 掷骰子
        public async Task<IActionResult> OnPostRollDiceAsync(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing") return Forbid();

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId) return NotFound();
            if (room.CurrentTurnPlayerId != playerId || player.IsBot) return Forbid();

            await ExecutePlayerTurn(room, player);
            await _context.SaveChangesAsync();
            await AdvanceTurn(room);
            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage(new { roomId, playerId });
        }

        // 离开游戏（直接删除玩家，不再保留机器人）
        public async Task<IActionResult> OnPostLeaveGameAsync(int roomId, int playerId)
        {
            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId || player.IsBot)
                return RedirectToPage("Play");

            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null) return RedirectToPage("Play");

            // 删除玩家
            _context.GamePlayers.Remove(player);
            room.CurrentPlayers--;

            // 如果房间无人，结束游戏
            if (room.CurrentPlayers == 0)
            {
                room.RoomStatus = "Finished";
                room.GameEndTime = DateTime.UtcNow;
            }
            else
            {
                // 如果当前回合是该玩家，切换回合
                if (room.CurrentTurnPlayerId == playerId)
                {
                    await AdvanceTurn(room);
                }
                // 如果房主离开，转移房主给下一个真人玩家
                if (room.RoomOwner == User.Identity?.Name)
                {
                    var nextOwner = await _context.GamePlayers
                        .Where(p => p.GameRoomId == roomId && !p.IsBot && p.Id != playerId)
                        .FirstOrDefaultAsync();
                    if (nextOwner != null)
                        room.RoomOwner = nextOwner.PlayerName;
                    else
                    {
                        // 如果没有真人玩家，可将房主设给机器人或置空
                        var anyPlayer = await _context.GamePlayers
                            .Where(p => p.GameRoomId == roomId && p.Id != playerId)
                            .FirstOrDefaultAsync();
                        if (anyPlayer != null) room.RoomOwner = anyPlayer.PlayerName;
                    }
                }
            }

            await _context.SaveChangesAsync();
            // 通知其他玩家刷新
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage("Play");
        }

        private async Task ExecutePlayerTurn(GameRooms room, GamePlayer player)
        {
            int dice = new Random().Next(1, 7);
            player.CurrentPosition = (player.CurrentPosition + dice) % room.MaxCount;
            if (player.CurrentPosition % 5 == 0) player.Gold += 100;
            if (player.CurrentPosition % 7 == 0) player.HP -= 10;
        }

        private async Task AdvanceTurn(GameRooms room)
        {
            var players = await _context.GamePlayers
                .Where(p => p.GameRoomId == room.Id)
                .OrderBy(p => p.Id)
                .ToListAsync();

            for (int i = 0; i < players.Count * 2; i++)
            {
                var currentPlayer = players.FirstOrDefault(p => p.Id == room.CurrentTurnPlayerId);
                if (currentPlayer == null) break;

                int idx = players.FindIndex(p => p.Id == currentPlayer.Id);
                int nextIdx = (idx + 1) % players.Count;
                room.CurrentTurnPlayerId = players[nextIdx].Id;
                await _context.SaveChangesAsync();

                if (!players[nextIdx].IsBot) break;

                await ExecutePlayerTurn(room, players[nextIdx]);
                await _context.SaveChangesAsync();
            }
        }
    }
}
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

        private static readonly List<EquipmentDef> EquipmentPool = new()
        {
            new EquipmentDef("木剑",     "Weapon"),
            new EquipmentDef("铁剑",     "Weapon"),
            new EquipmentDef("短弓",     "Weapon"),
            new EquipmentDef("魔法杖",   "Weapon"),
            new EquipmentDef("布衣",     "Dress"),
            new EquipmentDef("皮甲",     "Dress"),
            new EquipmentDef("锁子甲",   "Dress"),
            new EquipmentDef("皮帽",     "Helmet"),
            new EquipmentDef("铁盔",     "Helmet"),
            new EquipmentDef("斗笠",     "Helmet"),
            new EquipmentDef("石戒指",   "Ring"),
            new EquipmentDef("银戒指",   "Ring"),
            new EquipmentDef("金戒指",   "Ring"),
            new EquipmentDef("铁护腕",   "Armring"),
            new EquipmentDef("铜护腕",   "Armring"),
            new EquipmentDef("魔法项链", "Necklace"),
            new EquipmentDef("骨制项链", "Necklace"),
        };
        private record EquipmentDef(string Name, string Slot);

        public GameModel(AppDbContext context, IHubContext<GameRoomHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        public GameRooms Room { get; set; }
        public List<GamePlayer> Players { get; set; } = new();
        public GamePlayer CurrentPlayer { get; set; }
        public bool IsMyTurn { get; set; }
        public Dictionary<int, GameCells> BoardEvents { get; set; } = new();

        // 最近的游戏日志（用于视图展示）
        public List<GameLogs> RecentLogs { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int roomId, int playerId)
        {
            Room = await _context.GameRooms.FindAsync(roomId);
            if (Room == null || Room.RoomStatus != "Playing") return NotFound();

            Players = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId)
                .OrderBy(p => p.Id)
                .ToListAsync();

            CurrentPlayer = Players.FirstOrDefault(p => p.Id == playerId);
            if (CurrentPlayer == null) return Forbid();

            IsMyTurn = (Room.CurrentTurnPlayerId == playerId) && !CurrentPlayer.IsBot;

            // 读取最近 20 条日志
            RecentLogs = await _context.GameLogs
                .Where(l => l.GameRoomId == roomId)
                .OrderByDescending(l => l.CreatedTime)
                .Take(20)
                .ToListAsync();

            // 初始化棋盘事件
            var allEvents = await _context.GameCells.ToListAsync();
            if (allEvents.Any())
            {
                var rng = new Random(roomId);
                for (int i = 0; i < Room.MaxCount; i++)
                {
                    BoardEvents[i] = allEvents[rng.Next(allEvents.Count)];
                }
            }

            return Page();
        }

        // 真人玩家掷骰子
        public async Task<IActionResult> OnPostRollDiceAsync(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing") return Forbid();

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId) return NotFound();
            if (room.CurrentTurnPlayerId != playerId || player.IsBot) return Forbid();

            // 真人回合执行（内含日志记录）
            await ExecutePlayerTurn(room, player);
            await _context.SaveChangesAsync();

            // 机器人回合
            await AdvanceTurn(room);
            await _context.SaveChangesAsync();

            // 通知所有玩家刷新（日志已在数据库）
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage(new { roomId, playerId });
        }

        // 离开游戏
        public async Task<IActionResult> OnPostLeaveGameAsync(int roomId, int playerId)
        {
            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId || player.IsBot)
                return RedirectToPage("Play");

            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null) return RedirectToPage("Play");

            _context.GamePlayers.Remove(player);
            room.CurrentPlayers--;

            if (room.CurrentPlayers == 0)
            {
                room.RoomStatus = "Finished";
                room.GameEndTime = DateTime.UtcNow;
            }
            else
            {
                if (room.CurrentTurnPlayerId == playerId)
                    await AdvanceTurn(room);
                if (room.RoomOwner == User.Identity?.Name)
                {
                    var nextOwner = await _context.GamePlayers
                        .Where(p => p.GameRoomId == roomId && !p.IsBot && p.Id != playerId)
                        .FirstOrDefaultAsync();
                    if (nextOwner != null) room.RoomOwner = nextOwner.PlayerName;
                }
            }

            // 记录离开日志
            _context.GameLogs.Add(new GameLogs
            {
                GameRoomId = roomId,
                PlayerId = playerId,
                PlayerName = player.PlayerName,
                LogType = "Leave",
                Message = $"{player.PlayerName} 离开了游戏。",
                CreatedTime = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage("Play");
        }

        // 执行单个玩家回合，并将日志添加到上下文
        private async Task ExecutePlayerTurn(GameRooms room, GamePlayer player)
        {
            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;
            int oldPos = player.CurrentPosition ?? 1;
            int newPos = (oldPos + dice) % maxCount;
            player.CurrentPosition = newPos;

            // 记录移动日志
            _context.GameLogs.Add(new GameLogs
            {
                GameRoomId = room.Id,
                PlayerId = player.Id,
                PlayerName = player.PlayerName,
                LogType = "Move",
                Message = $"{player.PlayerName} 掷出了 {dice} 点，移动到格子 {newPos}。",
                CreatedTime = DateTime.UtcNow
            });

            var allPlayers = await _context.GamePlayers
                .Where(p => p.GameRoomId == room.Id)
                .ToListAsync();

            // 触发格子事件并记录日志
            if (BoardEvents.TryGetValue(newPos, out var evt))
            {
                string eventMsg = ApplyEvent(room, player, evt, allPlayers, maxCount);
                if (!string.IsNullOrEmpty(eventMsg))
                {
                    _context.GameLogs.Add(new GameLogs
                    {
                        GameRoomId = room.Id,
                        PlayerId = player.Id,
                        PlayerName = player.PlayerName,
                        LogType = "Event",
                        Message = eventMsg,
                        CreatedTime = DateTime.UtcNow
                    });
                }

                // 传送事件递归处理
                if (evt.EventType == "Teleport")
                {
                    int teleportedPos = player.CurrentPosition ?? newPos - 1;
                    if (BoardEvents.TryGetValue(teleportedPos, out var newEvt))
                    {
                        string additionalMsg = ApplyEvent(room, player, newEvt, allPlayers, maxCount);
                        if (!string.IsNullOrEmpty(additionalMsg))
                        {
                            _context.GameLogs.Add(new GameLogs
                            {
                                GameRoomId = room.Id,
                                PlayerId = player.Id,
                                PlayerName = player.PlayerName,
                                LogType = "Event",
                                Message = additionalMsg,
                                CreatedTime = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
        }

        // 应用事件效果，返回事件描述文本
        private string ApplyEvent(GameRooms room, GamePlayer player, GameCells evt, List<GamePlayer> allPlayers, int maxCount)
        {
            var rnd = new Random();
            switch (evt.EventType)
            {
                case "Gold":
                    int gold = evt.Value + rnd.Next(-20, 21);
                    player.Gold = Math.Max(0, (player.Gold ?? 0) + gold);
                    return $"金币{(gold >= 0 ? "+" : "")}{gold}。";

                case "HP":
                    int hp = evt.Value + rnd.Next(-5, 6);
                    player.HP = Math.Min(player.HPMAX ?? 100, Math.Max(0, (player.HP ?? 100) + hp));
                    return $"生命{(hp >= 0 ? "+" : "")}{hp}。";

                case "MP":
                    int mp = evt.Value + rnd.Next(-5, 6);
                    player.MP = Math.Min(player.MPMAX ?? 100, Math.Max(0, (player.MP ?? 100) + mp));
                    return $"魔法{(mp >= 0 ? "+" : "")}{mp}。";

                case "Teleport":
                    int shift = evt.Value;
                    int oldPos = player.CurrentPosition ?? 1;
                    player.CurrentPosition = (oldPos - 1 + shift + maxCount) % maxCount + 1;
                    return $"被传送到了格子 {player.CurrentPosition}。";

                case "Steal":
                    int stealBase = evt.Value;
                    var others = allPlayers.Where(p => p.Id != player.Id).ToList();
                    int totalStolen = 0;
                    foreach (var other in others)
                    {
                        int stolen = Math.Min(stealBase + rnd.Next(0, 21), other.Gold ?? 0);
                        other.Gold -= stolen;
                        totalStolen += stolen;
                    }
                    player.Gold = (player.Gold ?? 0) + totalStolen;
                    return $"偷取了 {totalStolen} 金币。";

                case "Trap":
                    return "踩到了陷阱，下回合无法行动。";

                case "Random":
                    int subType = rnd.Next(4);
                    if (subType == 0)
                    {
                        int g = rnd.Next(50, 151);
                        player.Gold += g;
                        return $"随机获得 {g} 金币。";
                    }
                    else if (subType == 1)
                    {
                        int h = rnd.Next(10, 31);
                        player.HP = Math.Min(player.HPMAX ?? 100, (player.HP ?? 100) + h);
                        return $"随机恢复 {h} 生命。";
                    }
                    else if (subType == 2)
                    {
                        int m = rnd.Next(10, 31);
                        player.MP = Math.Min(player.MPMAX ?? 100, (player.MP ?? 100) + m);
                        return $"随机恢复 {m} 魔法。";
                    }
                    else
                    {
                        int s = rnd.Next(-5, 6);
                        player.CurrentPosition = (player.CurrentPosition - 1 + s + maxCount) % maxCount + 1;
                        return $"随机移动 {s} 格。";
                    }

                case "Equipment":
                    var equip = EquipmentPool[rnd.Next(EquipmentPool.Count)];
                    switch (equip.Slot)
                    {
                        case "Weapon": player.Weapon = equip.Name; break;
                        case "Dress": player.Dress = equip.Name; break;
                        case "Helmet": player.Helmet = equip.Name; break;
                        case "Ring": player.Ring = equip.Name; break;
                        case "Armring": player.Armring = equip.Name; break;
                        case "Necklace": player.Necklace = equip.Name; break;
                    }
                    return $"获得了装备「{equip.Name}」。";

                default:
                    return "";
            }
        }

        // 切换回合并自动处理机器人回合（日志已在 ExecutePlayerTurn 中添加）
        private async Task AdvanceTurn(GameRooms room)
        {
            var players = await _context.GamePlayers
                .Where(p => p.GameRoomId == room.Id)
                .OrderBy(p => p.Id)
                .ToListAsync();

            for (int i = 0; i < players.Count * 2; i++)
            {
                var cur = players.FirstOrDefault(p => p.Id == room.CurrentTurnPlayerId);
                if (cur == null) break;

                int idx = players.FindIndex(p => p.Id == cur.Id);
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
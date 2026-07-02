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
        public Dictionary<int, GameCells> BoardEvents { get; set; } = new();
        public List<GameLogs> RecentLogs { get; set; } = new();

        private async Task InitBoardEvents(int roomId)
        {
            var allEvents = await _context.GameCells.ToListAsync();
            if (allEvents.Any() && Room != null)
            {
                var rng = new Random(roomId);
                for (int i = 0; i < Room.MaxCount; i++)
                    BoardEvents[i] = allEvents[rng.Next(allEvents.Count)];
            }
        }

        public async Task<IActionResult> OnGetAsync(int roomId, int playerId)
        {
            Room = await _context.GameRooms.FindAsync(roomId);
            if (Room == null || Room.RoomStatus != "Playing") return NotFound();

            Players = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId).OrderBy(p => p.Id).ToListAsync();

            CurrentPlayer = Players.FirstOrDefault(p => p.Id == playerId);
            if (CurrentPlayer == null) return Forbid();

            IsMyTurn = (Room.CurrentTurnPlayerId == playerId) && !CurrentPlayer.IsBot && CurrentPlayer.TrapTurns == 0;

            RecentLogs = await _context.GameLogs
                .Where(l => l.GameRoomId == roomId)
                .OrderBy(l => l.CreatedTime).ThenBy(l => l.Id)
                .ToListAsync();

            await InitBoardEvents(roomId);
            return Page();
        }

        // 预览掷骰子（不保存）
        public async Task<IActionResult> OnPostRollDicePreview(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing")
                return new JsonResult(new { error = "游戏未进行" });

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });
            if (room.CurrentTurnPlayerId != playerId || player.IsBot)
                return new JsonResult(new { error = "不是你的回合" });
            if (player.TrapTurns > 0)
                return new JsonResult(new { error = "你被陷阱困住，无法行动" });

            Room = room;
            await InitBoardEvents(roomId);

            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;
            int newPos = ((player.CurrentPosition ?? 1) - 1 + dice) % maxCount + 1;

            BoardEvents.TryGetValue(newPos - 1, out GameCells cellEvent);

            return new JsonResult(new
            {
                dice,
                newPosition = newPos,
                eventType = cellEvent?.EventType,
                eventName = cellEvent?.EventName,
                eventDescription = cellEvent?.Description,
                eventValue = cellEvent?.Value,
                currentEquipment = GetCurrentEquipment(player, cellEvent?.EventType),
                newEquipmentName = cellEvent?.EventName
            });
        }

        // 提交回合（真正执行）
        public async Task<IActionResult> OnPostCommitTurn(int roomId, int playerId, int dice, bool replaceEquipment)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing")
                return new JsonResult(new { error = "游戏未进行" });

            Room = room;
            await InitBoardEvents(roomId);

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });
            if (room.CurrentTurnPlayerId != playerId || player.IsBot)
                return new JsonResult(new { error = "不是你的回合" });
            if (player.TrapTurns > 0)
                return new JsonResult(new { error = "无法行动" });

            // 陷阱二次检查
            if (player.TrapTurns > 0)
            {
                player.TrapTurns--;
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                    PlayerName = player.PlayerName,
                    LogType = "Trap",
                    Message = $"{player.PlayerName} 因陷阱无法行动。",
                    CreatedTime = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                await AdvanceTurn(room);
                await _context.SaveChangesAsync();
                await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
                return new JsonResult(new { success = true });
            }

            int maxCount = room.MaxCount ?? 100;
            int newPos = ((player.CurrentPosition ?? 1) - 1 + dice) % maxCount + 1;
            player.CurrentPosition = newPos;

            int? uidLog = int.TryParse(player.UserId, out var userId) ? userId : null;
            _context.GameLogs.Add(new GameLogs
            {
                GameRoomId = room.Id,
                PlayerId = uidLog,
                PlayerName = player.PlayerName,
                LogType = "Move",
                Message = $"{player.PlayerName} 掷出了 {dice} 点，移动到格子 {newPos}。",
                CreatedTime = DateTime.UtcNow
            });

            var allPlayers = await _context.GamePlayers.Where(p => p.GameRoomId == room.Id).ToListAsync();

            if (BoardEvents.TryGetValue(newPos - 1, out var evt))
            {
                string eventMsg = null;
                bool isEquip = IsEquipmentSlot(evt.EventType);

                if (isEquip)
                {
                    if (replaceEquipment)
                    {
                        eventMsg = ApplyEvent(room, player, evt, allPlayers, maxCount);
                    }
                    else
                    {
                        eventMsg = $"保留了当前装备，放弃了「{evt.EventName}」。";
                    }
                }
                else
                {
                    eventMsg = ApplyEvent(room, player, evt, allPlayers, maxCount);
                }

                if (!string.IsNullOrEmpty(eventMsg))
                {
                    _context.GameLogs.Add(new GameLogs
                    {
                        GameRoomId = room.Id,
                        PlayerId = uidLog,
                        PlayerName = player.PlayerName,
                        LogType = "Event",
                        Message = eventMsg,
                        CreatedTime = DateTime.UtcNow
                    });
                }

                // 传送连锁
                if (evt.EventType == "Teleport")
                {
                    int teleportedPos = player.CurrentPosition ?? newPos;
                    if (BoardEvents.TryGetValue(teleportedPos - 1, out var newEvt))
                    {
                        string additionalMsg = ApplyEvent(room, player, newEvt, allPlayers, maxCount);
                        if (!string.IsNullOrEmpty(additionalMsg))
                        {
                            _context.GameLogs.Add(new GameLogs
                            {
                                GameRoomId = room.Id,
                                PlayerId = uidLog,
                                PlayerName = player.PlayerName,
                                LogType = "Event",
                                Message = additionalMsg,
                                CreatedTime = DateTime.UtcNow
                            });
                        }
                    }
                }
            }

            await _context.SaveChangesAsync();
            await AdvanceTurn(room);
            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return new JsonResult(new { success = true });
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

            int? uid = int.TryParse(player.UserId, out var id) ? id : null;
            _context.GameLogs.Add(new GameLogs
            {
                GameRoomId = roomId,
                PlayerId = uid,
                PlayerName = player.PlayerName,
                LogType = "Leave",
                Message = $"{player.PlayerName} 离开了游戏。",
                CreatedTime = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage("Play");
        }

        private bool IsEquipmentSlot(string eventType)
        {
            return eventType == "Weapon" || eventType == "Dress" || eventType == "Helmet" ||
                   eventType == "Ring" || eventType == "Armring" || eventType == "Necklace";
        }

        private string GetCurrentEquipment(GamePlayer player, string eventType)
        {
            return eventType switch
            {
                "Weapon" => player.Weapon ?? "无",
                "Dress" => player.Dress ?? "无",
                "Helmet" => player.Helmet ?? "无",
                "Ring" => player.Ring ?? "无",
                "Armring" => player.Armring ?? "无",
                "Necklace" => player.Necklace ?? "无",
                _ => null
            };
        }

        // 应用事件效果（所有分支都返回值）
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
                    int old = player.CurrentPosition ?? 1;
                    player.CurrentPosition = (old - 1 + shift + maxCount) % maxCount + 1;
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
                    player.TrapTurns = 1;
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
                // 装备获得（增加属性）
                case "Weapon":
                    player.Weapon = evt.EventName;
                    player.DC = (player.DC ?? 0) + evt.Value;
                    return $"获得了武器「{evt.EventName}」，攻击+{evt.Value}。";
                case "Dress":
                    player.Dress = evt.EventName;
                    player.AC = (player.AC ?? 0) + evt.Value;
                    return $"获得了衣服「{evt.EventName}」，防御+{evt.Value}。";
                case "Helmet":
                    player.Helmet = evt.EventName;
                    player.AC = (player.AC ?? 0) + evt.Value;
                    return $"获得了头盔「{evt.EventName}」，防御+{evt.Value}。";
                case "Ring":
                    player.Ring = evt.EventName;
                    player.DC = (player.DC ?? 0) + evt.Value;
                    return $"获得了戒指「{evt.EventName}」，攻击+{evt.Value}。";
                case "Armring":
                    player.Armring = evt.EventName;
                    player.AC = (player.AC ?? 0) + evt.Value;
                    return $"获得了护腕「{evt.EventName}」，防御+{evt.Value}。";
                case "Necklace":
                    player.Necklace = evt.EventName;
                    int bonus = evt.Value;
                    player.DC = (player.DC ?? 0) + bonus / 2;
                    player.AC = (player.AC ?? 0) + bonus - bonus / 2;
                    return $"获得了项链「{evt.EventName}」，攻击+{bonus / 2}，防御+{bonus - bonus / 2}。";
                case "LoseEquipment":
                    string slot = evt.EventName;
                    switch (slot)
                    {
                        case "Weapon": player.Weapon = "无"; player.DC = (player.DC ?? 0) - 3; break;
                        case "Dress": player.Dress = "无"; player.AC = (player.AC ?? 0) - 3; break;
                        case "Helmet": player.Helmet = "无"; player.AC = (player.AC ?? 0) - 3; break;
                        case "Ring": player.Ring = "无"; player.DC = (player.DC ?? 0) - 2; break;
                        case "Armring": player.Armring = "无"; player.AC = (player.AC ?? 0) - 2; break;
                        case "Necklace": player.Necklace = "无"; player.DC = (player.DC ?? 0) - 2; player.AC = (player.AC ?? 0) - 2; break;
                    }
                    return $"失去了装备「{slot}」，属性降低。";
                default:
                    return ""; // 保证所有路径返回值
            }
        }

        // 回合推进（含机器人）
        private async Task AdvanceTurn(GameRooms room)
        {
            var players = await _context.GamePlayers
                .Where(p => p.GameRoomId == room.Id).OrderBy(p => p.Id).ToListAsync();

            int maxLoops = players.Count * 3;
            for (int i = 0; i < maxLoops; i++)
            {
                var cur = players.FirstOrDefault(p => p.Id == room.CurrentTurnPlayerId);
                if (cur == null) break;

                int idx = players.FindIndex(p => p.Id == cur.Id);
                int nextIdx = (idx + 1) % players.Count;
                room.CurrentTurnPlayerId = players[nextIdx].Id;
                await _context.SaveChangesAsync();

                var nextPlayer = players[nextIdx];

                // 机器人
                if (nextPlayer.IsBot)
                {
                    await ExecuteBotTurn(room, nextPlayer);
                    await _context.SaveChangesAsync();
                    continue;
                }

                // 真人陷阱自动跳过
                if (!nextPlayer.IsBot && nextPlayer.TrapTurns > 0)
                {
                    nextPlayer.TrapTurns--;
                    int? uid = int.TryParse(nextPlayer.UserId, out var uidVal) ? uidVal : null;
                    _context.GameLogs.Add(new GameLogs
                    {
                        GameRoomId = room.Id,
                        PlayerId = uid,
                        PlayerName = nextPlayer.PlayerName,
                        LogType = "Trap",
                        Message = $"{nextPlayer.PlayerName} 因陷阱无法行动。",
                        CreatedTime = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                    continue;
                }

                break;
            }
        }

        // 机器人回合（不弹窗）
        private async Task ExecuteBotTurn(GameRooms room, GamePlayer bot)
        {
            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;
            int newPos = ((bot.CurrentPosition ?? 1) - 1 + dice) % maxCount + 1;
            bot.CurrentPosition = newPos;

            int? uid = null;
            _context.GameLogs.Add(new GameLogs
            {
                GameRoomId = room.Id,
                PlayerId = uid,
                PlayerName = bot.PlayerName,
                LogType = "Move",
                Message = $"{bot.PlayerName} 掷出了 {dice} 点，移动到格子 {newPos}。",
                CreatedTime = DateTime.UtcNow
            });

            var allPlayers = await _context.GamePlayers.Where(p => p.GameRoomId == room.Id).ToListAsync();

            if (BoardEvents.TryGetValue(newPos - 1, out var evt))
            {
                string eventMsg = ApplyEvent(room, bot, evt, allPlayers, maxCount);
                if (!string.IsNullOrEmpty(eventMsg))
                {
                    _context.GameLogs.Add(new GameLogs
                    {
                        GameRoomId = room.Id,
                        PlayerId = uid,
                        PlayerName = bot.PlayerName,
                        LogType = "Event",
                        Message = eventMsg,
                        CreatedTime = DateTime.UtcNow
                    });
                }

                if (evt.EventType == "Teleport")
                {
                    int teleportedPos = bot.CurrentPosition ?? newPos;
                    if (BoardEvents.TryGetValue(teleportedPos - 1, out var newEvt))
                    {
                        string additionalMsg = ApplyEvent(room, bot, newEvt, allPlayers, maxCount);
                        if (!string.IsNullOrEmpty(additionalMsg))
                        {
                            _context.GameLogs.Add(new GameLogs
                            {
                                GameRoomId = room.Id,
                                PlayerId = uid,
                                PlayerName = bot.PlayerName,
                                LogType = "Event",
                                Message = additionalMsg,
                                CreatedTime = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
        }
    }
}
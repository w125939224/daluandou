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

            int? currentEquipValue = null;
            if (cellEvent != null && IsEquipmentSlot(cellEvent.EventType))
            {
                string curEquipName = GetCurrentEquipment(player, cellEvent.EventType);
                if (curEquipName != null && curEquipName != "无")
                    currentEquipValue = await GetEquipmentValue(cellEvent.EventType, curEquipName);
            }

            return new JsonResult(new
            {
                dice,
                newPosition = newPos,
                eventType = cellEvent?.EventType,
                eventName = cellEvent?.EventName,
                eventDescription = cellEvent?.Description,
                eventValue = cellEvent?.Value,
                currentEquipment = GetCurrentEquipment(player, cellEvent?.EventType),
                currentEquipmentValue = currentEquipValue,
                newEquipmentName = cellEvent?.EventName,
                newEquipmentValue = cellEvent?.Value,
                currentDC = player.DC,
                currentAC = player.AC
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
                        eventMsg = await ApplyEquipmentReplace(player, evt);
                    else
                        eventMsg = $"保留了当前装备，放弃了「{evt.EventName}」。";
                }
                else
                {
                    eventMsg = await ApplyEvent(room, player, evt, allPlayers, maxCount);
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

                if (evt.EventType == "Teleport")
                {
                    int teleportedPos = player.CurrentPosition ?? newPos;
                    if (BoardEvents.TryGetValue(teleportedPos - 1, out var newEvt))
                    {
                        string additionalMsg = await ApplyEvent(room, player, newEvt, allPlayers, maxCount);
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

        private async Task<int> GetEquipmentValue(string slotType, string equipmentName)
        {
            var cell = await _context.GameCells
                .FirstOrDefaultAsync(c => c.EventType == slotType && c.EventName == equipmentName);
            return cell?.Value ?? 0;
        }

        // 装备替换核心逻辑，返回详细的日志消息
        private async Task<string> ApplyEquipmentReplace(GamePlayer player, GameCells newEvent)
        {
            string slot = newEvent.EventType;
            string newName = newEvent.EventName;
            int newValue = newEvent.Value;
            string oldName = GetCurrentEquipment(player, slot);

            // 获取属性名称和槽位中文名
            string attrName = GetAttrName(slot);
            string slotName = GetSlotName(slot);

            int oldValue = 0;
            if (oldName != null && oldName != "无")
            {
                oldValue = await GetEquipmentValue(slot, oldName);
                SubtractAttributes(player, slot, oldValue);
            }

            SetEquipment(player, slot, newName);
            AddAttributes(player, slot, newValue);

            if (oldName != null && oldName != "无")
            {
                return $"更换了装备：将 {oldName}（{attrName}{oldValue}）替换为 {newName}（{attrName}{newValue}）。";
            }
            else
            {
                return $"获得了{slotName}「{newName}」，{attrName}+{newValue}。";
            }
        }

        // 获取属性中文名（攻击力/防御力）
        private string GetAttrName(string slot)
        {
            return slot switch
            {
                "Weapon" => "攻击力",
                "Ring" => "攻击力",
                "Dress" => "防御力",
                "Helmet" => "防御力",
                "Armring" => "防御力",
                "Necklace" => "攻击力和防御力",
                _ => "属性"
            };
        }

        // 获取槽位中文名
        private string GetSlotName(string slot)
        {
            return slot switch
            {
                "Weapon" => "武器",
                "Dress" => "衣服",
                "Helmet" => "头盔",
                "Ring" => "戒指",
                "Armring" => "护腕",
                "Necklace" => "项链",
                _ => "装备"
            };
        }

        private void SetEquipment(GamePlayer player, string slot, string name)
        {
            switch (slot)
            {
                case "Weapon": player.Weapon = name; break;
                case "Dress": player.Dress = name; break;
                case "Helmet": player.Helmet = name; break;
                case "Ring": player.Ring = name; break;
                case "Armring": player.Armring = name; break;
                case "Necklace": player.Necklace = name; break;
            }
        }

        private void AddAttributes(GamePlayer player, string slot, int value)
        {
            if (value <= 0) return;
            switch (slot)
            {
                case "Weapon":
                case "Ring":
                    player.DC = (player.DC ?? 0) + value;
                    break;
                case "Dress":
                case "Helmet":
                case "Armring":
                    player.AC = (player.AC ?? 0) + value;
                    break;
                case "Necklace":
                    player.DC = (player.DC ?? 0) + value / 2;
                    player.AC = (player.AC ?? 0) + value - value / 2;
                    break;
            }
        }

        private void SubtractAttributes(GamePlayer player, string slot, int value)
        {
            if (value <= 0) return;
            switch (slot)
            {
                case "Weapon":
                case "Ring":
                    player.DC = Math.Max(0, (player.DC ?? 0) - value);
                    break;
                case "Dress":
                case "Helmet":
                case "Armring":
                    player.AC = Math.Max(0, (player.AC ?? 0) - value);
                    break;
                case "Necklace":
                    int dcDeduction = value / 2;
                    int acDeduction = value - dcDeduction;
                    player.DC = Math.Max(0, (player.DC ?? 0) - dcDeduction);
                    player.AC = Math.Max(0, (player.AC ?? 0) - acDeduction);
                    break;
            }
        }

        // 应用事件效果（非装备事件）
        private async Task<string> ApplyEvent(GameRooms room, GamePlayer player, GameCells evt, List<GamePlayer> allPlayers, int maxCount)
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
                    if (subType == 0) { int g = rnd.Next(50, 151); player.Gold += g; return $"随机获得 {g} 金币。"; }
                    else if (subType == 1) { int h = rnd.Next(10, 31); player.HP = Math.Min(player.HPMAX ?? 100, (player.HP ?? 100) + h); return $"随机恢复 {h} 生命。"; }
                    else if (subType == 2) { int m = rnd.Next(10, 31); player.MP = Math.Min(player.MPMAX ?? 100, (player.MP ?? 100) + m); return $"随机恢复 {m} 魔法。"; }
                    else { int s = rnd.Next(-5, 6); player.CurrentPosition = ((player.CurrentPosition ?? 1) - 1 + s + maxCount) % maxCount + 1; return $"随机移动 {s} 格。"; }

                case "LoseEquipment":
                    string slot = evt.EventName;
                    string oldEquipName = GetCurrentEquipment(player, slot);
                    if (oldEquipName != null && oldEquipName != "无")
                    {
                        int loseValue = await GetEquipmentValue(slot, oldEquipName);
                        SubtractAttributes(player, slot, loseValue);
                        SetEquipment(player, slot, "无");
                        return $"失去了装备「{oldEquipName}」，{GetAttrName(slot)}-{loseValue}。";
                    }
                    else
                    {
                        return "没有装备可失去。";
                    }

                default:
                    return "";
            }
        }

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

                if (nextPlayer.IsBot)
                {
                    await ExecuteBotTurn(room, nextPlayer);
                    await _context.SaveChangesAsync();
                    continue;
                }

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

        // 机器人回合
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
                string eventMsg;
                if (IsEquipmentSlot(evt.EventType))
                    eventMsg = await ApplyEquipmentReplace(bot, evt);
                else
                    eventMsg = await ApplyEvent(room, bot, evt, allPlayers, maxCount);

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
                        string additionalMsg = await ApplyEvent(room, bot, newEvt, allPlayers, maxCount);
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
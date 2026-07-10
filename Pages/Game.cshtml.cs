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
using System.Text.Json;
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

        private List<Magic> Skills = new();

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

            bool alive = CurrentPlayer.HP > 0;
            IsMyTurn = (Room.CurrentTurnPlayerId == playerId) && !CurrentPlayer.IsBot && alive && CurrentPlayer.TrapTurns == 0;

            RecentLogs = await _context.GameLogs
                .Where(l => l.GameRoomId == roomId)
                .OrderBy(l => l.CreatedTime).ThenBy(l => l.Id)
                .ToListAsync();

            Skills = await _context.Magics.ToListAsync();
            await InitBoardEvents(roomId);
            return Page();
        }

        // 获取可用行动（技能、平砍、卡牌）
        public async Task<IActionResult> OnPostGetActions(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing")
                return new JsonResult(new { error = "游戏未进行" });

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });
            if (room.CurrentTurnPlayerId != playerId || player.IsBot || player.TrapTurns > 0 || player.HP <= 0)
                return new JsonResult(new { error = "无法行动" });

            var allPlayers = await _context.GamePlayers
                .Where(p => p.GameRoomId == roomId && p.HP > 0)
                .ToListAsync();

            Skills = await _context.Magics.ToListAsync();
            var learnedIds = ParseLearnedSkillIds(player.LearnedMagicIds);
            var learnedSkills = Skills.Where(s => learnedIds.Contains(s.Id)).ToList();

            var meleeTargets = allPlayers
                .Where(p => p.Id != playerId && Math.Abs(p.CurrentPosition - player.CurrentPosition) <= 1)
                .Select(p => new { p.Id, p.PlayerName, distance = Math.Abs(p.CurrentPosition - player.CurrentPosition) })
                .ToList();

            var availableSkills = learnedSkills.Select(s =>
            {
                var targets = GetSkillTargets(player, s, allPlayers);
                return new
                {
                    s.Id,
                    s.Name,
                    s.MpCost,
                    s.Range,
                    s.EffectType,
                    s.BaseValue,
                    available = player.MP >= s.MpCost && targets.Count > 0,
                    targets
                };
            }).Where(s => s.available).ToList();

            // 卡牌
            var cards = ParseCards(player.CardsJson);
            var availableCards = new List<object>();
            if (cards.ContainsKey("DoubleDice") && cards["DoubleDice"] > 0)
                availableCards.Add(new { type = "DoubleDice", name = "双重骰子", description = "下次掷骰子时可投两次" });
            if (cards.ContainsKey("TrapEnemy") && cards["TrapEnemy"] > 0)
            {
                var trapTargets = allPlayers.Where(p => p.Id != playerId)
                    .Select(p => new { p.Id, p.PlayerName, distance = Math.Abs(p.CurrentPosition - player.CurrentPosition) })
                    .ToList();
                availableCards.Add(new { type = "TrapEnemy", name = "禁锢咒", description = "指定一名敌人下回合休息", targets = trapTargets });
            }
            if (cards.ContainsKey("Forward1") && cards["Forward1"] > 0)
                availableCards.Add(new { type = "Forward1", name = "前进卡", description = "立即前进1格" });
            if (cards.ContainsKey("Backward1") && cards["Backward1"] > 0)
                availableCards.Add(new { type = "Backward1", name = "后退卡", description = "立即后退1格" });

            return new JsonResult(new
            {
                canMove = true,
                skills = availableSkills,
                melee = new { name = "平砍", available = meleeTargets.Count > 0, targets = meleeTargets },
                cards = availableCards
            });
        }

        private List<object> GetSkillTargets(GamePlayer caster, Magic skill, List<GamePlayer> allPlayers)
        {
            var targets = new List<object>();
            foreach (var p in allPlayers)
            {
                if (p.Id == caster.Id && skill.BaseValue > 0) continue;
                int distance = Math.Abs(p.CurrentPosition - caster.CurrentPosition);
                if (distance <= skill.Range)
                    targets.Add(new { p.Id, p.PlayerName, distance });
            }
            return targets;
        }

        // 预览掷骰子（不变）
        public async Task<IActionResult> OnPostRollDicePreview(int roomId, int playerId)
        {
            var room = await _context.GameRooms.FindAsync(roomId);
            if (room == null || room.RoomStatus != "Playing")
                return new JsonResult(new { error = "游戏未进行" });

            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });
            if (room.CurrentTurnPlayerId != playerId || player.IsBot || player.TrapTurns > 0 || player.HP <= 0)
                return new JsonResult(new { error = "无法行动" });

            Room = room;
            await InitBoardEvents(roomId);

            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;
            int newPos = ((player.CurrentPosition) - 1 + dice) % maxCount + 1;

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

        // 提交回合（移动/普攻/技能）
        public async Task<IActionResult> OnPostCommitTurn(int roomId, int playerId, string actionType, int dice = 0, int skillId = 0, int targetId = 0, bool replaceEquipment = true, bool useDoubleDice = false)
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
            if (player.TrapTurns > 0 || player.HP <= 0)
                return new JsonResult(new { error = "无法行动" });

            Skills = await _context.Magics.ToListAsync();

            if (actionType == "move")
            {
                int maxCount = room.MaxCount ?? 100;
                if (useDoubleDice)
                {
                    ConsumeCard(player, "DoubleDice");
                    int dice2 = new Random().Next(1, 7);
                    dice += dice2;
                    _context.GameLogs.Add(new GameLogs
                    {
                        GameRoomId = room.Id,
                        PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                        PlayerName = player.PlayerName,
                        LogType = "Card",
                        Message = $"{player.PlayerName} 使用了双重骰子，额外掷出 {dice2} 点。",
                        CreatedTime = DateTime.UtcNow
                    });
                }
                int newPos = ((player.CurrentPosition) - 1 + dice) % maxCount + 1;
                await ExecutePlayerTurn(room, player, dice, newPos, replaceEquipment);
            }
            else if (actionType == "melee")
            {
                var target = await _context.GamePlayers.FindAsync(targetId);
                if (target == null || target.GameRoomId != roomId)
                    return new JsonResult(new { error = "目标无效" });
                if (Math.Abs(target.CurrentPosition - player.CurrentPosition) > 1)
                    return new JsonResult(new { error = "目标不在近战范围" });

                int damage = Math.Max(1, player.DC - target.AC);
                target.HP -= damage;
                string msg = $"{player.PlayerName} 平砍了 {target.PlayerName}，造成 {damage} 点伤害。";
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                    PlayerName = player.PlayerName,
                    LogType = "Melee",
                    Message = msg,
                    CreatedTime = DateTime.UtcNow
                });
                CheckDeath(target, room);
            }
            else if (actionType == "skill")
            {
                var learnedIds = ParseLearnedSkillIds(player.LearnedMagicIds);
                if (!learnedIds.Contains(skillId))
                    return new JsonResult(new { error = "你还没有学会此技能" });

                var skill = Skills.FirstOrDefault(s => s.Id == skillId);
                if (skill == null) return new JsonResult(new { error = "技能不存在" });
                if (player.MP < skill.MpCost) return new JsonResult(new { error = "魔法不足" });

                var target = await _context.GamePlayers.FindAsync(targetId);
                if (target == null || target.GameRoomId != roomId)
                    return new JsonResult(new { error = "目标无效" });

                int dist = Math.Abs(player.CurrentPosition - target.CurrentPosition);
                if (dist > skill.Range) return new JsonResult(new { error = "目标不在技能范围内" });

                player.MP -= skill.MpCost;
                string actionDesc = ApplySkillEffect(player, target, skill);

                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                    PlayerName = player.PlayerName,
                    LogType = "Skill",
                    Message = actionDesc,
                    CreatedTime = DateTime.UtcNow
                });
                CheckDeath(target, room);
            }
            else return new JsonResult(new { error = "无效行动类型" });

            if (player.HP <= 0)
            {
                player.HP = player.HPMAX;
                player.MP = player.MPMAX;
                player.CurrentPosition = 1;
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                    PlayerName = player.PlayerName,
                    LogType = "Death",
                    Message = $"{player.PlayerName} 意外死亡，返回起点。",
                    CreatedTime = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            await AdvanceTurn(room);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return new JsonResult(new { success = true });
        }

        // 使用卡牌（禁锢咒、前进/后退卡）
        public async Task<IActionResult> OnPostUseCard(int roomId, int playerId, string cardType, int targetId = 0)
        {
            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });

            var cards = ParseCards(player.CardsJson);
            if (!cards.ContainsKey(cardType) || cards[cardType] <= 0)
                return new JsonResult(new { error = "没有该卡牌" });

            if (cardType == "DoubleDice")
                return new JsonResult(new { error = "请在掷骰子时选择使用双重骰子" });

            cards[cardType]--;
            if (cards[cardType] <= 0) cards.Remove(cardType);
            player.CardsJson = JsonSerializer.Serialize(cards);

            string logMsg = "";
            switch (cardType)
            {
                case "TrapEnemy":
                    var target = await _context.GamePlayers.FindAsync(targetId);
                    if (target != null)
                    {
                        target.TrapTurns = 1;
                        logMsg = $"{player.PlayerName} 对 {target.PlayerName} 使用了禁锢咒，目标下回合无法行动。";
                    }
                    break;
                case "Forward1":
                    int maxF = Room?.MaxCount ?? 100;
                    player.CurrentPosition = (player.CurrentPosition % maxF) + 1;
                    logMsg = $"{player.PlayerName} 使用前进卡，移动到格子 {player.CurrentPosition}。";
                    break;
                case "Backward1":
                    int maxB = Room?.MaxCount ?? 100;
                    player.CurrentPosition = ((player.CurrentPosition - 2 + maxB) % maxB) + 1;
                    logMsg = $"{player.PlayerName} 使用后退卡，移动到格子 {player.CurrentPosition}。";
                    break;
                default:
                    return new JsonResult(new { error = "未知卡牌" });
            }

            if (!string.IsNullOrEmpty(logMsg))
            {
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = roomId,
                    PlayerId = int.TryParse(player.UserId, out var uid) ? uid : null,
                    PlayerName = player.PlayerName,
                    LogType = "Card",
                    Message = logMsg,
                    CreatedTime = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true });
        }

        // 获取随机商店物品（每次踩中商店时随机抽取 3~5 件）
        public async Task<IActionResult> OnPostGetShopItems(int roomId)
        {
            var allItems = await _context.ShopItems.ToListAsync();
            var rnd = new Random();
            int take = Math.Min(allItems.Count, rnd.Next(3, 6));
            var selectedItems = allItems.OrderBy(x => rnd.Next()).Take(take).ToList();
            return new JsonResult(selectedItems.Select(i => new {
                i.Id,
                i.ItemType,
                i.ItemId,
                i.ItemName,
                i.Price,
                i.Description
            }));
        }

        // 购买物品
        public async Task<IActionResult> OnPostBuyItem(int roomId, int playerId, int shopItemId)
        {
            var player = await _context.GamePlayers.FindAsync(playerId);
            if (player == null || player.GameRoomId != roomId)
                return new JsonResult(new { error = "玩家不存在" });

            var item = await _context.ShopItems.FindAsync(shopItemId);
            if (item == null) return new JsonResult(new { error = "物品不存在" });

            if (player.Gold < item.Price)
                return new JsonResult(new { error = "金币不足" });

            player.Gold -= item.Price;

            switch (item.ItemType)
            {
                case "Equipment":
                    var cell = await _context.GameCells.FindAsync(item.ItemId);
                    if (cell != null && IsEquipmentSlot(cell.EventType))
                    {
                        await ApplyEquipmentReplace(player, cell);
                    }
                    break;
                case "Magic":
                    var magic = await _context.Magics.FindAsync(item.ItemId);
                    if (magic != null)
                    {
                        var learned = ParseLearnedSkillIds(player.LearnedMagicIds);
                        if (!learned.Contains(magic.Id))
                        {
                            learned.Add(magic.Id);
                            player.LearnedMagicIds = string.Join(",", learned);
                        }
                    }
                    break;
                case "CardItem":
                    var cards = ParseCards(player.CardsJson);
                    string cardType = item.ItemName switch
                    {
                        "双重骰子" => "DoubleDice",
                        "禁锢咒" => "TrapEnemy",
                        "前进卡" => "Forward1",
                        "后退卡" => "Backward1",
                        _ => null
                    };
                    if (cardType != null)
                    {
                        if (cards.ContainsKey(cardType))
                            cards[cardType]++;
                        else
                            cards[cardType] = 1;
                        player.CardsJson = JsonSerializer.Serialize(cards);
                    }
                    break;
            }

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true, gold = player.Gold });
        }

        // 辅助方法：消耗卡牌
        private void ConsumeCard(GamePlayer player, string cardType)
        {
            var cards = ParseCards(player.CardsJson);
            if (cards.ContainsKey(cardType) && cards[cardType] > 0)
            {
                cards[cardType]--;
                if (cards[cardType] <= 0) cards.Remove(cardType);
                player.CardsJson = JsonSerializer.Serialize(cards);
            }
        }

        private Dictionary<string, int> ParseCards(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, int>();
            try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json); }
            catch { return new Dictionary<string, int>(); }
        }

        private void CheckDeath(GamePlayer target, GameRooms room)
        {
            if (target.HP <= 0)
            {
                target.HP = 0;
                target.CurrentPosition = 1;
                target.HP = target.HPMAX;
                target.MP = target.MPMAX;
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = int.TryParse(target.UserId, out var uid) ? uid : null,
                    PlayerName = target.PlayerName,
                    LogType = "Death",
                    Message = $"{target.PlayerName} 被击败，返回起点。",
                    CreatedTime = DateTime.UtcNow
                });
            }
        }

        private string ApplySkillEffect(GamePlayer caster, GamePlayer target, Magic skill)
        {
            int finalValue;
            string typeName = skill.BaseValue > 0 ? "伤害" : "治疗";

            if (skill.EffectType == "Fixed")
            {
                finalValue = Math.Abs(skill.BaseValue);
                if (skill.BaseValue > 0) target.HP -= finalValue;
                else target.HP = Math.Min(target.HPMAX, target.HP + finalValue);
            }
            else
            {
                int factor = Math.Abs(skill.BaseValue);
                if (skill.BaseValue > 0)
                {
                    finalValue = Math.Max(1, (caster.DC - target.AC) * factor);
                    target.HP -= finalValue;
                }
                else
                {
                    finalValue = caster.DC * factor;
                    target.HP = Math.Min(target.HPMAX, target.HP + finalValue);
                }
            }
            string action = skill.BaseValue > 0 ? "造成" : "恢复";
            return $"{caster.PlayerName} 对 {target.PlayerName} 使用了 {skill.Name}，{action}了 {finalValue} 点{typeName}。";
        }

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

        private bool IsEquipmentSlot(string eventType) =>
            eventType == "Weapon" || eventType == "Dress" || eventType == "Helmet" ||
            eventType == "Ring" || eventType == "Armring" || eventType == "Necklace";

        private string GetCurrentEquipment(GamePlayer player, string eventType) => eventType switch
        {
            "Weapon" => player.Weapon ?? "无",
            "Dress" => player.Dress ?? "无",
            "Helmet" => player.Helmet ?? "无",
            "Ring" => player.Ring ?? "无",
            "Armring" => player.Armring ?? "无",
            "Necklace" => player.Necklace ?? "无",
            _ => null
        };

        private async Task<int> GetEquipmentValue(string slotType, string equipmentName)
        {
            var cell = await _context.GameCells
                .FirstOrDefaultAsync(c => c.EventType == slotType && c.EventName == equipmentName);
            return cell?.Value ?? 0;
        }

        private async Task<string> ExecutePlayerTurn(GameRooms room, GamePlayer player, int dice, int newPos, bool replaceEquipment)
        {
            int? uidLog = int.TryParse(player.UserId, out var userId) ? userId : null;

            if (player.TrapTurns > 0)
            {
                player.TrapTurns--;
                _context.GameLogs.Add(new GameLogs
                {
                    GameRoomId = room.Id,
                    PlayerId = uidLog,
                    PlayerName = player.PlayerName,
                    LogType = "Trap",
                    Message = $"{player.PlayerName} 因陷阱无法行动。",
                    CreatedTime = DateTime.UtcNow
                });
                return "trap_skipped";
            }

            player.CurrentPosition = newPos;
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
            int maxCount = room.MaxCount ?? 100;

            if (BoardEvents.TryGetValue(newPos - 1, out var evt))
            {
                string eventMsg = null;
                bool isEquip = IsEquipmentSlot(evt.EventType);
                if (isEquip)
                {
                    eventMsg = replaceEquipment ? await ApplyEquipmentReplace(player, evt) : $"保留了当前装备，放弃了「{evt.EventName}」。";
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
                    int teleportedPos = player.CurrentPosition;
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
            return "ok";
        }

        private async Task<string> ApplyEquipmentReplace(GamePlayer player, GameCells newEvent)
        {
            string slot = newEvent.EventType;
            string newName = newEvent.EventName;
            int newValue = newEvent.Value;
            string oldName = GetCurrentEquipment(player, slot);
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
            return oldName != null && oldName != "无"
                ? $"更换了装备：将 {oldName}（{attrName}{oldValue}）替换为 {newName}（{attrName}{newValue}）。"
                : $"获得了{slotName}「{newName}」，{attrName}+{newValue}。";
        }

        private string GetAttrName(string slot) => slot switch
        {
            "Weapon" => "攻击力",
            "Ring" => "攻击力",
            "Dress" => "防御力",
            "Helmet" => "防御力",
            "Armring" => "防御力",
            "Necklace" => "攻击力和防御力",
            _ => "属性"
        };
        private string GetSlotName(string slot) => slot switch
        {
            "Weapon" => "武器",
            "Dress" => "衣服",
            "Helmet" => "头盔",
            "Ring" => "戒指",
            "Armring" => "护腕",
            "Necklace" => "项链",
            _ => "装备"
        };

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
                case "Weapon": case "Ring": player.DC += value; break;
                case "Dress": case "Helmet": case "Armring": player.AC += value; break;
                case "Necklace": player.DC += value; player.AC += value; break;
            }
        }

        private void SubtractAttributes(GamePlayer player, string slot, int value)
        {
            if (value <= 0) return;
            switch (slot)
            {
                case "Weapon": case "Ring": player.DC = Math.Max(0, player.DC - value); break;
                case "Dress": case "Helmet": case "Armring": player.AC = Math.Max(0, player.AC - value); break;
                case "Necklace": player.DC = Math.Max(0, player.DC - value); player.AC = Math.Max(0, player.AC - value); break;
            }
        }

        private async Task<string> ApplyEvent(GameRooms room, GamePlayer player, GameCells evt, List<GamePlayer> allPlayers, int maxCount)
        {
            var rnd = new Random();
            switch (evt.EventType)
            {
                case "Gold":
                    int gold = evt.Value + rnd.Next(-20, 21);
                    player.Gold = Math.Max(0, player.Gold + gold);
                    return $"金币{(gold >= 0 ? "+" : "")}{gold}。";
                case "HP":
                    int hp = evt.Value + rnd.Next(-5, 6);
                    player.HP = Math.Min(player.HPMAX, Math.Max(0, player.HP + hp));
                    return $"生命{(hp >= 0 ? "+" : "")}{hp}。";
                case "MP":
                    int mp = evt.Value + rnd.Next(-5, 6);
                    player.MP = Math.Min(player.MPMAX, Math.Max(0, player.MP + mp));
                    return $"魔法{(mp >= 0 ? "+" : "")}{mp}。";
                case "Teleport":
                    int shift = evt.Value;
                    int old = player.CurrentPosition;
                    player.CurrentPosition = (old - 1 + shift + maxCount) % maxCount + 1;
                    return $"被传送到了格子 {player.CurrentPosition}。";
                case "Steal":
                    int stealBase = evt.Value;
                    var others = allPlayers.Where(p => p.Id != player.Id).ToList();
                    int totalStolen = 0;
                    foreach (var other in others)
                    {
                        int stolen = Math.Min(stealBase + rnd.Next(0, 21), other.Gold);
                        other.Gold -= stolen;
                        totalStolen += stolen;
                    }
                    player.Gold += totalStolen;
                    return $"偷取了 {totalStolen} 金币。";
                case "Trap":
                    player.TrapTurns = 1;
                    return "踩到了陷阱，下回合无法行动。";
                case "Random":
                    int subType = rnd.Next(4);
                    if (subType == 0) { int g = rnd.Next(50, 151); player.Gold += g; return $"随机获得 {g} 金币。"; }
                    else if (subType == 1) { int h = rnd.Next(10, 31); player.HP = Math.Min(player.HPMAX, player.HP + h); return $"随机恢复 {h} 生命。"; }
                    else if (subType == 2) { int m = rnd.Next(10, 31); player.MP = Math.Min(player.MPMAX, player.MP + m); return $"随机恢复 {m} 魔法。"; }
                    else { int s = rnd.Next(-5, 6); player.CurrentPosition = ((player.CurrentPosition) - 1 + s + maxCount) % maxCount + 1; return $"随机移动 {s} 格。"; }
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
                    else return "没有装备可失去。";
                case "LearnMagic":
                    if (evt.MagicId != null)
                    {
                        var skill = Skills.FirstOrDefault(s => s.Id == evt.MagicId.Value);
                        if (skill != null)
                        {
                            var list = ParseLearnedSkillIds(player.LearnedMagicIds);
                            if (!list.Contains(skill.Id))
                            {
                                list.Add(skill.Id);
                                player.LearnedMagicIds = string.Join(",", list);
                                return $"学会了技能「{skill.Name}」！";
                            }
                            return $"你已经学会了技能「{skill.Name}」。";
                        }
                    }
                    return "";
                case "Shop":
                    return "你进入了商店。";
                default:
                    return "";
            }
        }

        private List<int> ParseLearnedSkillIds(string learnedIds)
        {
            if (string.IsNullOrWhiteSpace(learnedIds)) return new List<int>();
            return learnedIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(id => int.TryParse(id, out var i) ? i : 0)
                             .Where(i => i > 0)
                             .ToList();
        }

        private async Task AdvanceTurn(GameRooms room)
        {
            var players = await _context.GamePlayers.Where(p => p.GameRoomId == room.Id).OrderBy(p => p.Id).ToListAsync();
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
                    await ExecutePlayerTurn(room, nextPlayer, 0, 0, false);
                    await _context.SaveChangesAsync();
                    continue;
                }
                break;
            }
        }

        private async Task ExecuteBotTurn(GameRooms room, GamePlayer bot)
        {
            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;
            int newPos = ((bot.CurrentPosition) - 1 + dice) % maxCount + 1;
            await ExecutePlayerTurn(room, bot, dice, newPos, true);
        }
    }
}
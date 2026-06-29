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

        // 装备池
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

        [TempData]
        public string ActionMessage { get; set; }

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

            // 基于房间 ID 的固定随机种子，确保每次进入棋盘事件一致
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

            // 执行回合并生成描述信息
            string message = await ExecutePlayerTurn(room, player);
            ActionMessage = message;

            await _context.SaveChangesAsync();

            // 切换回合并自动处理机器人
            await AdvanceTurn(room);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage(new { roomId, playerId });
        }

        // 离开游戏（保留原有逻辑）
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
                {
                    await AdvanceTurn(room);
                }
                if (room.RoomOwner == User.Identity?.Name)
                {
                    var nextOwner = await _context.GamePlayers
                        .Where(p => p.GameRoomId == roomId && !p.IsBot && p.Id != playerId)
                        .FirstOrDefaultAsync();
                    if (nextOwner != null)
                        room.RoomOwner = nextOwner.PlayerName;
                    else
                    {
                        var anyPlayer = await _context.GamePlayers
                            .Where(p => p.GameRoomId == roomId && p.Id != playerId)
                            .FirstOrDefaultAsync();
                        if (anyPlayer != null) room.RoomOwner = anyPlayer.PlayerName;
                    }
                }
            }

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group($"game_{roomId}").SendAsync("UpdateGame", roomId);
            return RedirectToPage("Play");
        }

        // 执行单个玩家回合，返回行动描述
        private async Task<string> ExecutePlayerTurn(GameRooms room, GamePlayer player)
        {
            int dice = new Random().Next(1, 7);
            int maxCount = room.MaxCount ?? 100;      // 处理可空类型
            int oldPos = player.CurrentPosition ?? 0;
            int newPos = (oldPos + dice) % maxCount;
            player.CurrentPosition = newPos;

            string message = $"{player.PlayerName} 掷出了 {dice} 点，移动到格子 {newPos + 1}。";

            var allPlayers = await _context.GamePlayers
                .Where(p => p.GameRoomId == room.Id)
                .ToListAsync();

            if (BoardEvents.TryGetValue(newPos, out var evt))
            {
                string eventMsg = ApplyEvent(room, player, evt, allPlayers, maxCount);
                if (!string.IsNullOrEmpty(eventMsg))
                    message += " " + eventMsg;

                // 传送事件后递归处理新格子事件
                if (evt.EventType == "Teleport")
                {
                    int teleportedPos = player.CurrentPosition ?? newPos;
                    if (BoardEvents.TryGetValue(teleportedPos, out var newEvt))
                    {
                        string additionalMsg = ApplyEvent(room, player, newEvt, allPlayers, maxCount);
                        if (!string.IsNullOrEmpty(additionalMsg))
                            message += " 然后" + additionalMsg;
                    }
                }
            }

            return message;
        }

        // 应用事件效果，返回描述文本
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
                    int oldPos = player.CurrentPosition ?? 0;
                    player.CurrentPosition = (oldPos + shift + room.MaxCount) % room.MaxCount;
                    return $"被传送到了格子 {player.CurrentPosition + 1}。";

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
                    // 暂未实现禁锢效果，只返回描述
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
                        player.CurrentPosition = (player.CurrentPosition + s + room.MaxCount) % room.MaxCount;
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

        // 切换回合，自动处理所有连续的机器人回合
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

                // 机器人回合自动执行（不生成对外提示，如有需要可记录日志）
                await ExecutePlayerTurn(room, players[nextIdx]);
                await _context.SaveChangesAsync();
            }
        }
    }
}
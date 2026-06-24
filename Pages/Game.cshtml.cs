using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using daluandou.Data;
using daluandou.Models;

namespace daluandou.Pages
{
    [Authorize]
    public class GameModel : PageModel
    {
        private readonly AppDbContext _context;

        public GameModel(AppDbContext context)
        {
            _context = context;
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

            IsMyTurn = (Room.CurrentTurnPlayerId == playerId);
            return Page();
        }
    }
}
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using daluandou.Data;
using daluandou.Models;
using Microsoft.EntityFrameworkCore;

namespace daluandou.Pages
{
    public class ChatHub : Hub
    {
        private const int HALL_ROOM_ID = 1;

        private static readonly ConcurrentDictionary<string, OnlineUser> _onlineUsers = new();
        private readonly AppDbContext _db;

        public ChatHub(AppDbContext db) => _db = db;

        public class OnlineUser
        {
            public string Username { get; set; } = string.Empty;
            public string ConnectionId { get; set; } = string.Empty;
            public int CurrentRoomId { get; set; } = HALL_ROOM_ID;
        }

        // 公共静态方法获取所有在线用户
        public static List<OnlineUser> GetAllOnlineUsers()
        {
            return _onlineUsers.Values.ToList();
        }

        // 公共静态方法获取特定房间在线人数
        public static int GetRoomOnlineCount(int roomId)
        {
            return _onlineUsers.Values.Count(u => u.CurrentRoomId == roomId);
        }

        // 新增：检查房间是否存在的公共方法
        public async Task<object> CheckRoomExists(int roomId)
        {
            if (roomId == HALL_ROOM_ID)
            {
                return new { exists = true, hasPassword = false, isMine = false };
            }

            var room = await _db.ChatRooms.FindAsync(roomId);
            if (room == null)
            {
                return new { exists = false };
            }

            // 获取当前用户
            var currentUser = Context.GetHttpContext()?.Session.GetString("Username");
            bool isMine = room.CreateUser == currentUser;

            return new
            {
                exists = true,
                hasPassword = !string.IsNullOrEmpty(room.Password),
                isMine = isMine
            };
        }

        public override async Task OnConnectedAsync()
        {
            var user = Context.GetHttpContext()?.Session.GetString("Username");
            if (string.IsNullOrEmpty(user))
            {
                await Clients.Caller.SendAsync("ShowError", "请先登录");
                Context.Abort();
                return;
            }

            _onlineUsers[Context.ConnectionId] = new OnlineUser
            {
                Username = user,
                ConnectionId = Context.ConnectionId,
                CurrentRoomId = HALL_ROOM_ID
            };

            await Groups.AddToGroupAsync(Context.ConnectionId, HALL_ROOM_ID.ToString());

            await Clients.Group(HALL_ROOM_ID.ToString()).SendAsync("UserJoined", user);
            await UpdateRoomOnlineUsers(HALL_ROOM_ID);
            await UpdateRoomOnlineCount(HALL_ROOM_ID);
            await BroadcastAllRoomsCount();
            await Clients.Caller.SendAsync("WelcomeMessage", $"欢迎 {user}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            if (_onlineUsers.TryRemove(Context.ConnectionId, out var u))
            {
                await Clients.Group(u.CurrentRoomId.ToString()).SendAsync("UserLeft", u.Username);
                await UpdateRoomOnlineUsers(u.CurrentRoomId);
                await UpdateRoomOnlineCount(u.CurrentRoomId);
                await BroadcastAllRoomsCount();
            }
            await base.OnDisconnectedAsync(ex);
        }

        // 创建房间时可以设置密码
        public async Task CreateRoom(string roomName, string? password = null)
        {
            if (!_onlineUsers.TryGetValue(Context.ConnectionId, out var u)) return;

            var room = new ChatRoom
            {
                RoomName = roomName,
                Password = string.IsNullOrEmpty(password) ? null : password,
                CreateUser = u.Username,
                CreateTime = DateTime.Now
            };
            _db.ChatRooms.Add(room);
            await _db.SaveChangesAsync();

            await JoinRoom(room.Id, null); // 创建者不需要输入密码
            await Clients.All.SendAsync("RoomListUpdated"); // 通知所有人更新房间列表
            await BroadcastAllRoomsCount(); // 广播所有房间人数更新
        }

        public async Task DeleteRoom(int roomId)
        {
            if (!_onlineUsers.TryGetValue(Context.ConnectionId, out var u)) return;

            var room = await _db.ChatRooms.FindAsync(roomId);
            if (room == null) return;
            if (room.CreateUser != u.Username) return;

            var messages = _db.ChatMessages.Where(m => m.RoomId == roomId);
            _db.ChatMessages.RemoveRange(messages);
            _db.ChatRooms.Remove(room);
            await _db.SaveChangesAsync();

            await Clients.All.SendAsync("RoomListUpdated"); // 通知所有人更新房间列表
            await BroadcastAllRoomsCount(); // 广播所有房间人数更新
        }

        // 按ID加入房间方法
        public async Task<bool> JoinRoom(int roomId, string? password = null)
        {
            if (!_onlineUsers.TryGetValue(Context.ConnectionId, out var u))
                return false;

            // 大厅不需要密码
            if (roomId == HALL_ROOM_ID)
            {
                return await JoinRoomInternal(roomId, u);
            }

            // 检查房间是否存在
            var room = await _db.ChatRooms.FindAsync(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinRoomFailed", "房间不存在");
                return false;
            }

            // 验证密码（如果房间有密码）
            if (!string.IsNullOrEmpty(room.Password))
            {
                // 创建者不需要输入密码
                if (room.CreateUser != u.Username && room.Password != password)
                {
                    await Clients.Caller.SendAsync("JoinRoomFailed", "密码错误");
                    return false;
                }
            }

            return await JoinRoomInternal(roomId, u);
        }

        // 内部加入房间逻辑
        private async Task<bool> JoinRoomInternal(int roomId, OnlineUser u)
        {
            int oldRoomId = u.CurrentRoomId;

            if (oldRoomId == roomId)
            {
                await UpdateRoomOnlineUsers(roomId);
                await UpdateRoomOnlineCount(roomId);
                return true;
            }

            // 离开旧房间
            if (oldRoomId > 0)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoomId.ToString());
                await Clients.Group(oldRoomId.ToString()).SendAsync("UserLeft", u.Username);
                await UpdateRoomOnlineUsers(oldRoomId);
                await UpdateRoomOnlineCount(oldRoomId);
            }

            // 加入新房间
            u.CurrentRoomId = roomId;
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());

            string roomName = roomId == HALL_ROOM_ID ? "聊天大厅" :
                (await _db.ChatRooms.FindAsync(roomId))?.RoomName ?? $"房间{roomId}";

            var history = await _db.ChatMessages
                .Where(x => x.RoomId == roomId)
                .OrderBy(x => x.SendTime)
                .Take(100)
                .ToListAsync();

            await Clients.Caller.SendAsync("LoadHistoryMessages", history);
            await Clients.Caller.SendAsync("WelcomeMessage", $"✅ 已进入：{roomName}");
            await Clients.Group(roomId.ToString()).SendAsync("UserJoined", u.Username);

            await UpdateRoomOnlineUsers(roomId);
            await UpdateRoomOnlineCount(roomId);
            await BroadcastAllRoomsCount();

            return true;
        }

        public async Task SendMessage(string content)
        {
            if (!_onlineUsers.TryGetValue(Context.ConnectionId, out var u) || u.CurrentRoomId <= 0)
                return;

            var msg = new ChatMessage
            {
                RoomId = u.CurrentRoomId,
                SenderUsername = u.Username,
                Content = content,
                SendTime = DateTime.Now
            };

            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync();

            var time = msg.SendTime.ToString("HH:mm:ss");
            await Clients.Group(u.CurrentRoomId.ToString()).SendAsync("ReceiveMessage", u.Username, content, time);
        }

        private async Task UpdateRoomOnlineUsers(int roomId)
        {
            if (roomId <= 0) return;

            var roomUsers = _onlineUsers.Values
                .Where(u => u.CurrentRoomId == roomId)
                .Select(u => u.Username)
                .Distinct()
                .ToList();

            await Clients.Group(roomId.ToString()).SendAsync("UpdateRoomOnlineUsers", roomUsers);
        }

        private async Task UpdateRoomOnlineCount(int roomId)
        {
            if (roomId <= 0) return;

            int count = GetRoomOnlineCount(roomId);

            foreach (var user in _onlineUsers.Values.Where(u => u.CurrentRoomId == roomId))
            {
                await Clients.Client(user.ConnectionId).SendAsync("RoomOnlineCountUpdated", roomId, count);
            }
        }

        // 向所有在线用户广播所有房间的人数更新
        private async Task BroadcastAllRoomsCount()
        {
            // 获取所有公开房间的人数
            var roomsCount = new Dictionary<int, int>();

            // 先添加大厅人数
            roomsCount[HALL_ROOM_ID] = GetRoomOnlineCount(HALL_ROOM_ID);

            // 添加所有公开房间的人数
            var publicRooms = await _db.ChatRooms
                .Where(x => string.IsNullOrEmpty(x.Password))
                .Select(x => x.Id)
                .ToListAsync();

            foreach (var roomId in publicRooms)
            {
                roomsCount[roomId] = GetRoomOnlineCount(roomId);
            }

            // 向所有在线用户广播
            await Clients.All.SendAsync("AllRoomsCountUpdated", roomsCount);
        }
    }

    public class ChatRoomModel : PageModel
    {
        private readonly AppDbContext _db;
        private readonly IHubContext<ChatHub> _hubContext;

        // 注入IHubContext以便获取在线用户信息
        public ChatRoomModel(AppDbContext db, IHubContext<ChatHub> hubContext)
        {
            _db = db;
            _hubContext = hubContext;
        }

        public IActionResult OnGet()
        {
            var u = HttpContext.Session.GetString("Username");
            if (string.IsNullOrEmpty(u)) return Redirect("/Login");
            return Page();
        }

        // 返回房间列表时包含在线人数
        public async Task<IActionResult> OnGetRoomsAsync()
        {
            var currentUser = HttpContext.Session.GetString("Username");

            var rooms = await _db.ChatRooms
                .OrderByDescending(x => x.CreateTime)
                .Where(x => string.IsNullOrEmpty(x.Password) || x.CreateUser == currentUser)
                .Select(x => new
                {
                    id = x.Id,
                    roomName = x.RoomName,
                    createUser = x.CreateUser,
                    hasPassword = !string.IsNullOrEmpty(x.Password),
                    isMine = x.CreateUser == currentUser
                })
                .ToListAsync();

            // 计算每个房间的在线人数
            var onlineUsers = ChatHub.GetAllOnlineUsers();
            var result = rooms.Select(r => new
            {
                r.id,
                r.roomName,
                r.createUser,
                r.hasPassword,
                r.isMine,
                onlineCount = onlineUsers.Count(u => u.CurrentRoomId == r.id)
            }).ToList();

            return new JsonResult(result);
        }
    }
}
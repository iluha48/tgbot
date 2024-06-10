using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

namespace ConsoleApp2
{
    public class RegistrationState
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Patronymic { get; set; }
        public string PointName { get; set; }
        public bool IsAwaitingPointSelection { get; set; }
        public bool IsAwaitingLocation { get; set; }
    }

    public class UserState
    {
        public int InformationId { get; set; }
    }

    class Program
    {
        private static TelegramBotClient _botClient;
        private static ConcurrentDictionary<long, RegistrationState> _registrationStates = new ConcurrentDictionary<long, RegistrationState>();
        private static ConcurrentDictionary<long, UserState> _userStates = new ConcurrentDictionary<long, UserState>();
        private static ConcurrentDictionary<long, int> _photoCounts = new ConcurrentDictionary<long, int>();
        private static readonly object _fileLock = new object();
        private const long AdminChatId = -4210112235; // Замените на нужный ChatID администратора
        private const string ConnectionString = @"Data Source=.\SQLEXPRESS;Initial Catalog=telegrambot;Integrated Security=True"; // Замените на строку подключения к вашей базе данных

        static async Task Main(string[] args)
        {
            // Инициализация клиента бота
            _botClient = new TelegramBotClient("6565397184:AAEIgeI2tRkmM16vEhtesbgbxfEXIMiBdmQ");

            // Запуск обработчика сообщений
            _botClient.OnMessage += BotOnMessageReceived;
            _botClient.StartReceiving();

            // Планирование задач
            ScheduleTasks();

            Console.WriteLine("Bot started...");
            Console.ReadLine();

            // Остановка обработки сообщений перед завершением программы
            _botClient.StopReceiving();
        }

        private static void ScheduleTasks()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    var now = DateTime.UtcNow.AddHours(3); // Время по МСК
                    if (now.Hour == 8 && now.Minute == 0 || now.Hour == 10 && now.Minute == 0 || now.Hour == 12 && now.Minute == 0 || now.Hour == 14 && now.Minute == 0 || now.Hour == 16 && now.Minute == 0 || now.Hour == 18 && now.Minute == 0)
                    {
                        await RequestDataFromUsers();
                        await Task.Delay(3600000); // Wait for an hour
                    }
                    else if (now.Hour == 19 && now.Minute == 0)
                    {
                        await SendDataToAdmin();
                        await Task.Delay(3600000); // Wait for an hour
                    }
                    await Task.Delay(60000); // Check every minute
                }
            });
        }

        private static async Task RequestDataFromUsers()
        {
            var users = GetUsers();
            foreach (var user in users)
            {
                if (long.TryParse(user.ChatTelegramId, out var chatId))
                {
                    try { await _botClient.SendTextMessageAsync(chatId, "Пожалуйста, отправьте 5 фото (Отправлять фотографии надо по отдельности, в каждом отдельном сообщении)."); }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                    _photoCounts[chatId] = 0;

                    // Создаем запись Information для каждого пользователя
                    var infoId = AddInformation(new Information
                    {
                        CreatedDate = DateTime.Now,
                        UserId = user.UserId
                    });

                    // Сохраняем ID новой записи Information
                    _userStates[chatId] = new UserState { InformationId = infoId };
                }
            }
        }

        private static async Task SendDataToAdmin()
        {
            var today = DateTime.Today.Day;
            var informations = GetInformationsForToday(today);

            // Group information by user
            var groupedInformations = informations.GroupBy(info => info.User).ToList();

            foreach (var userGroup in groupedInformations)
            {
                var user = userGroup.Key;

                foreach (var info in userGroup)
                {
                    // Проверка на null
                    if (info?.User == null || info.User.Point == null)
                    {
                        continue; // Пропускаем итерацию, если info или связанные объекты null
                    }

                    try
                    {
                        var message = $"Пользователь: {info.User.Firstname} {info.User.Lastname} {info.User.Patronymic}\n" +
                                      $"Точка: {info.User.Point.PointName} Адрес: {info.User.Point.Adress} Координаты: {info.User.Point.Latitude} {info.User.Point.Longitude} \n" +
                                      $"Дата: {info.CreatedDate}\n" +
                                      $"Локация: {info.Latitude}, {info.Longitude}\n" +
                                      $"Проверка геолокации:  {IsWithinRange(info.Latitude, info.Longitude, info.User.Point.Latitude, info.User.Point.Longitude)}\n";

                        // Создаем список медиафайлов для отправки
                        var pathList = new List<string>();

                        // Добавляем каждую фотографию из info.Photos в список медиафайлов
                        foreach (var photo in info.Photos)
                        {
                            try
                            {
                                pathList.Add(photo.Path);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine($"Ошибка при добавлении фото в список: {e.Message}");
                            }
                        }

                        if (pathList.Count > 0)
                        {
                            // Создаем список медиафайлов для отправки
                            var mediaGroup = new List<IAlbumInputMedia>();

                            // Добавляем первую фотографию с текстом в виде подписи
                            var fileName = Path.GetFileName(pathList[0]);
                            var inputOnlineFile = new InputMedia(new FileStream(pathList[0], FileMode.Open, FileAccess.Read), fileName);
                            var inputMediaPhoto = new InputMediaPhoto(inputOnlineFile) { Caption = message, ParseMode = ParseMode.Html };
                            mediaGroup.Add(inputMediaPhoto);

                            // Добавляем оставшиеся фотографии без подписи
                            for (int i = 1; i < pathList.Count; i++)
                            {
                                fileName = Path.GetFileName(pathList[i]);
                                inputOnlineFile = new InputMedia(new FileStream(pathList[i], FileMode.Open, FileAccess.Read), fileName);
                                inputMediaPhoto = new InputMediaPhoto(inputOnlineFile);
                                mediaGroup.Add(inputMediaPhoto);
                            }

                            // Отправляем группу медиафайлов (фотографии) в одном сообщении
                            await _botClient.SendMediaGroupAsync(AdminChatId, mediaGroup);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Ошибка при отправке сообщения: {e.Message}");
                    }
                }
            }
        }







        private static async void BotOnMessageReceived(object sender, MessageEventArgs e)
        {
            try
            {
                var message = e.Message;

                if (message.Type == MessageType.Photo)
                {
                    if (!_photoCounts.ContainsKey(message.Chat.Id))
                    {
                        return;
                    }

                    var photoCount = _photoCounts[message.Chat.Id];
                    if (photoCount < 5)
                    {
                        var file = await _botClient.GetFileAsync(message.Photo.Last().FileId);
                        var filePath = $"./photos/{message.Chat.Id}_{file.FileId}.jpg";

                        lock (_fileLock) // блокировка доступа к файлу
                        {
                            using (var saveImageStream = System.IO.File.Open(filePath, FileMode.Create))
                            {
                                _botClient.DownloadFileAsync(file.FilePath, saveImageStream).Wait();
                            }
                        }

                        await SavePhotoInformationAsync(message.Chat.Id, filePath);

                        _photoCounts[message.Chat.Id] = photoCount + 1;

                        if (photoCount + 1 == 5)
                        {
                            var state = GetOrCreateState(message.Chat.Id);
                            state.IsAwaitingLocation = true;

                            var locationButton = new KeyboardButton("Отправить геолокацию") { RequestLocation = true };
                            var replyKeyboardMarkup = new ReplyKeyboardMarkup(locationButton) { ResizeKeyboard = true, OneTimeKeyboard = true };
                            await _botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, отправьте вашу геолокацию.", replyMarkup: replyKeyboardMarkup);
                        }
                    }
                }
                else if (message.Type == MessageType.Location)
                {
                    var state = GetOrCreateState(message.Chat.Id);
                    if (_photoCounts.ContainsKey(message.Chat.Id) && _photoCounts[message.Chat.Id] >= 5 && state.IsAwaitingLocation)
                    {
                        await SaveLocationInformationAsync(message.Chat.Id, (float)message.Location.Latitude, (float)message.Location.Longitude);

                        await _botClient.SendTextMessageAsync(message.Chat.Id, "Спасибо! Ваши данные успешно сохранены.", replyMarkup: new ReplyKeyboardRemove());
                        _photoCounts.TryRemove(message.Chat.Id, out _);
                        state.IsAwaitingLocation = false;
                    }
                }

                else if (message.Type == MessageType.Text)
                {
                    var existingUser = GetUserById(message.Chat.Id.ToString());

                    if (message.Text.Equals("/test", StringComparison.OrdinalIgnoreCase))
                    {
                        // Вызов метода SendRequests при получении команды /test
                        await RequestDataFromUsers();
                    }
                    if (message.Text.Equals("/test2", StringComparison.OrdinalIgnoreCase))
                    {
                        // Вызов метода SendRequests при получении команды /test2
                        await SendDataToAdmin();
                    }
                    else if (message.Text.StartsWith("/register"))
                    {
                        if (existingUser != null)
                        {
                            await _botClient.SendTextMessageAsync(message.Chat.Id, "Вы уже зарегистрированы.");
                            return;
                        }

                        _registrationStates[message.Chat.Id] = new RegistrationState();
                        await _botClient.SendTextMessageAsync(message.Chat.Id, "Введите ваше имя:");
                    }
                    else if (_registrationStates.ContainsKey(message.Chat.Id))
                    {
                        var state = _registrationStates[message.Chat.Id];

                        if (string.IsNullOrEmpty(state.FirstName))
                        {
                            state.FirstName = message.Text;
                            await _botClient.SendTextMessageAsync(message.Chat.Id, "Введите вашу фамилию:");
                        }
                        else if (string.IsNullOrEmpty(state.LastName))
                        {
                            state.LastName = message.Text;
                            await _botClient.SendTextMessageAsync(message.Chat.Id, "Введите ваше отчество:");
                        }
                        else if (string.IsNullOrEmpty(state.Patronymic))
                        {
                            state.Patronymic = message.Text;

                            var points = GetPoints();
                            var keyboardButtons = points.Select(p => new KeyboardButton(p.PointName)).ToArray();
                            var replyKeyboardMarkup = new ReplyKeyboardMarkup(keyboardButtons) { ResizeKeyboard = true, OneTimeKeyboard = true };

                            state.IsAwaitingPointSelection = true;
                            await _botClient.SendTextMessageAsync(message.Chat.Id, "Выберите пункт продаж:", replyMarkup: replyKeyboardMarkup);
                        }
                        else if (state.IsAwaitingPointSelection)
                        {
                            var point = GetPointByName(message.Text);
                            if (point == null)
                            {
                                await _botClient.SendTextMessageAsync(message.Chat.Id, "Пункт продаж не найден. Пожалуйста, выберите пункт продаж из меню.");
                            }
                            else
                            {
                                AddUser(new User
                                {
                                    Firstname = state.FirstName,
                                    Lastname = state.LastName,
                                    Patronymic = state.Patronymic,
                                    ChatTelegramId = message.Chat.Id.ToString(),
                                    PointId = point.PointId
                                });

                                _registrationStates.TryRemove(message.Chat.Id, out _);

                                await _botClient.SendTextMessageAsync(message.Chat.Id, "Вы успешно зарегистрированы.", replyMarkup: new ReplyKeyboardRemove());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private static async Task SavePhotoInformationAsync(long chatId, string filePath)
        {
            var user = GetUserById(chatId.ToString());
            if (user == null) return;

            if (!_userStates.TryGetValue(chatId, out var userState))
            {
                return;
            }

            AddPhoto(new Photo
            {
                Path = filePath,
                InformationId = userState.InformationId
            });
        }
        private static Point GetPointByName(string pointName)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("SELECT PointId FROM Points WHERE PointName = @PointName", connection);
                command.Parameters.AddWithValue("@PointName", pointName);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new Point
                        {
                            PointId = (int)reader["PointId"],
                            PointName = pointName
                        };
                    }
                }
            }

            return null;
        }

        private static async Task SaveLocationInformationAsync(long chatId, float latitude, float longitude)
        {
            var user = GetUserById(chatId.ToString());
            if (user == null) return;

            if (!_userStates.TryGetValue(chatId, out var userState))
            {
                return;
            }

            try
            {
                using (var connection = new SqlConnection(ConnectionString))
                {
                    var command = new SqlCommand("UPDATE Information SET Latitude = @Latitude, Longitude = @Longitude WHERE InformationId = @InformationId", connection);
                    command.Parameters.AddWithValue("@Latitude", latitude);
                    command.Parameters.AddWithValue("@Longitude", longitude);
                    command.Parameters.AddWithValue("@InformationId", userState.InformationId);
                    connection.Open();

                    var rowsAffected = await command.ExecuteNonQueryAsync();

                    if (rowsAffected == 0)
                    {
                        Console.WriteLine("Ошибка: Запись не обновлена. Проверьте правильность идентификатора информации.");
                    }
                    else
                    {
                        Console.WriteLine("Геолокация успешно сохранена.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при сохранении геолокации: {ex.Message}");
            }
        }

        private static RegistrationState GetOrCreateState(long chatId)
        {
            if (!_registrationStates.ContainsKey(chatId))
            {
                _registrationStates[chatId] = new RegistrationState();
            }
            return _registrationStates[chatId];
        }

        static bool IsWithinRange(double lat1, double lon1, double lat2, double lon2)
        {
            // Радиус Земли в километрах
            const double radius = 6371;

            // Преобразование градусов в радианы
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon1 - lon2);

            // Вычисление расстояния с помощью формулы гаверсинусов
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            double distance = radius * c;

            // Проверяем, находится ли точка 1 в пределах 200 метров от точки 2
            return distance <= 0.2; // Переводим 200 метров в километры
        }

        static double ToRadians(double angle)
        {
            return Math.PI * angle / 180.0;
        }

        // ADO.NET helper methods
        private static List<User> GetUsers()
        {
            var users = new List<User>();

            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("SELECT * FROM Users", connection);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        users.Add(new User
                        {
                            UserId = (int)reader["UserId"],
                            Firstname = reader["Firstname"].ToString(),
                            Lastname = reader["Lastname"].ToString(),
                            Patronymic = reader["Patronymic"].ToString(),
                            ChatTelegramId = reader["ChatTelegramId"].ToString(),
                            PointId = (int)reader["PointId"]
                        });
                    }
                }
            }

            return users;
        }

        private static User GetUserById(string chatTelegramId)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("SELECT * FROM Users WHERE ChatTelegramId = @ChatTelegramId", connection);
                command.Parameters.AddWithValue("@ChatTelegramId", chatTelegramId);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var user = new User
                        {
                            UserId = (int)reader["UserId"],
                            Firstname = reader["Firstname"].ToString(),
                            Lastname = reader["Lastname"].ToString(),
                            Patronymic = reader["Patronymic"].ToString(),
                            ChatTelegramId = reader["ChatTelegramId"].ToString(),
                            PointId = (int)reader["PointId"]
                        };

                        // Загрузите связанный объект Point
                        user.Point = GetPointById(user.PointId.ToString());

                        Console.WriteLine($"User found: {user.Firstname} {user.Lastname} {user.Patronymic}");
                        return user;
                    }
                    else
                    {
                        Console.WriteLine("User not found.");
                    }
                }
            }

            return null;
        }


        private static int AddInformation(Information information)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("INSERT INTO Information (CreatedDate, Latitude, Longitude, UserId) OUTPUT INSERTED.InformationId VALUES (@CreatedDate, 0, 0, @UserId)", connection); // Устанавливаем значения по умолчанию для Latitude и Longitude
                command.Parameters.AddWithValue("@CreatedDate", information.CreatedDate);
                command.Parameters.AddWithValue("@UserId", information.UserId);
                connection.Open();

                return (int)command.ExecuteScalar();
            }
        }


        private static List<Information> GetInformationsForToday(int today)
        {
            var informations = new List<Information>();

            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand(@"
            SELECT i.*, 
                   u.UserId AS UserId, u.Firstname AS UserFirstname, u.Lastname AS UserLastname, u.Patronymic AS UserPatronymic, 
                   p.PointId AS PointId, p.PointName AS PointName, p.Adress AS PointAdress, p.Latitude AS PointLatitude, p.Longitude AS PointLongitude,
                   ph.PhotoId AS PhotoId, ph.Path AS PhotoPath
            FROM Information i
            LEFT JOIN Users u ON i.UserId = u.UserId
            LEFT JOIN Points p ON u.PointId = p.PointId
            LEFT JOIN Photos ph ON i.InformationId = ph.InformationId
            WHERE DAY(i.CreatedDate) = @Today AND i.Latitude != 0", connection);
                command.Parameters.AddWithValue("@Today", today);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    var informationDictionary = new Dictionary<int, Information>();

                    while (reader.Read())
                    {
                        var infoId = (int)reader["InformationId"];
                        if (!informationDictionary.TryGetValue(infoId, out var info))
                        {
                            info = new Information
                            {
                                InformationId = infoId,
                                CreatedDate = (DateTime)reader["CreatedDate"],
                                Latitude = (double)reader["Latitude"],
                                Longitude = (double)reader["Longitude"],
                                User = new User
                                {
                                    UserId = (int)reader["UserId"],
                                    Firstname = reader["UserFirstname"].ToString(),
                                    Lastname = reader["UserLastname"].ToString(),
                                    Patronymic = reader["UserPatronymic"].ToString(),
                                    Point = new Point
                                    {
                                        PointId = (int)reader["PointId"],
                                        PointName = reader["PointName"].ToString(),
                                        Adress = reader["PointAdress"].ToString(),
                                        Latitude = (double)reader["PointLatitude"],
                                        Longitude = (double)reader["PointLongitude"]
                                    }
                                },
                                Photos = new List<Photo>()
                            };
                            informationDictionary.Add(infoId, info);
                        }

                        if (!reader.IsDBNull(reader.GetOrdinal("PhotoId")))
                        {
                            info.Photos.Add(new Photo
                            {
                                PhotoId = (int)reader["PhotoId"],
                                Path = reader["PhotoPath"].ToString()
                            });
                        }
                    }

                    informations = informationDictionary.Values.ToList();
                }
            }

            return informations;
        }


        private static void AddPhoto(Photo photo)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("INSERT INTO Photos (Path, InformationId) VALUES (@Path, @InformationId)", connection);
                command.Parameters.AddWithValue("@Path", photo.Path);
                command.Parameters.AddWithValue("@InformationId", photo.InformationId);
                connection.Open();

                command.ExecuteNonQuery();
            }
        }



        private static void AddUser(User user)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("INSERT INTO Users (Firstname, Lastname, Patronymic, ChatTelegramId, PointId) VALUES (@Firstname, @Lastname, @Patronymic, @ChatTelegramId, @PointId)", connection);
                command.Parameters.AddWithValue("@Firstname", user.Firstname);
                command.Parameters.AddWithValue("@Lastname", user.Lastname);
                command.Parameters.AddWithValue("@Patronymic", user.Patronymic);
                command.Parameters.AddWithValue("@ChatTelegramId", user.ChatTelegramId);
                command.Parameters.AddWithValue("@PointId", user.PointId);
                connection.Open();

                command.ExecuteNonQuery();
            }
        }

        private static List<Point> GetPoints()
        {
            var points = new List<Point>();

            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("SELECT * FROM Points", connection);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        points.Add(new Point
                        {
                            PointId = (int)reader["PointId"],
                            PointName = reader["PointName"].ToString(),
                            Adress = reader["Adress"].ToString(),
                            Latitude = (double)reader["Latitude"],
                            Longitude = (double)reader["Longitude"]
                        });
                    }
                }
            }

            return points;
        }

        private static Point GetPointById(string pointIdS)
        {
            int pointId;
            int.TryParse(pointIdS, out pointId);
            using (var connection = new SqlConnection(ConnectionString))
            {
                var command = new SqlCommand("SELECT * FROM Points WHERE PointId = @PointId", connection);
                command.Parameters.AddWithValue("@PointId", pointId);
                connection.Open();

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new Point
                        {
                            PointId = (int)reader["PointId"],
                            PointName = reader["PointName"].ToString(),
                            Adress = reader["Adress"].ToString(),
                            Latitude = (double)reader["Latitude"],
                            Longitude = (double)reader["Longitude"]
                        };
                    }
                }
            }

            return null;
        }
    }

    public class Information
    {
        public int InformationId { get; set; }
        public DateTime CreatedDate { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int UserId { get; set; }
        public virtual User User { get; set; }
        public virtual ICollection<Photo> Photos { get; set; }
    }

    public class Photo
    {
        public int PhotoId { get; set; }
        public string Path { get; set; }
        public int InformationId { get; set; }
        public virtual Information Information { get; set; }
    }

    public class User
    {
        public int UserId { get; set; }
        public string Firstname { get; set; }
        public string Lastname { get; set; }
        public string Patronymic { get; set; }
        public string ChatTelegramId { get; set; }
        public int PointId { get; set; }
        public virtual Point Point { get; set; }
        public virtual ICollection<Information> Informations { get; set; }
    }

    public class Point
    {
        public int PointId { get; set; }
        public string PointName { get; set; }
        public string Adress { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public virtual ICollection<User> Users { get; set; }
    }
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FiguresDotStore.Controllers
{
    internal interface IRedisClient
    {
        //поискал бы async API у Redis
        int Get(string type);
        void Set(string type, int current);
    }

    // зарегистрировать в DI container с помощью .AddScoped<FiguresStorage>(). Для UT выделить интерфейс, если необходимо.
    public sealed class FiguresStorage
    {
        // корректно сконфигурированный и готовый к использованию клиент Редиса
        private readonly IRedisClient _redisClient;

        public FiguresStorage(IRedisClient redisClient)
        {
            _redisClient = redisClient;
        }

        // такие проверки на доступность товара не сработают т.к. возможно параллельное создание заказов по одному типу геометрических фигур.
        // проверять надо на этапе резервации товара, после валидации самой корзины.

        // public bool CheckIfAvailable(string type, int count)
        // {
        //     return _redisClient.Get(type) >= count;
        // }

        public bool TryReserve(string type, int count)
        {
            var current = _redisClient.Get(type);
            var remains = current - count;
            if (remains <= 0)
            {
                return false;
            }

            _redisClient.Set(type, remains);
            return true;
        }
    }

    public sealed class Position
    {
        //вырезал лишние на мой взгляд поля SideA, SideB, SideC
        //позиция в корзине и сущность фигура, обладающая тремя координатами - разные сущности.
        
        //заменил string Type на явное использование класса Figure
        public Figure Figure { get; set; }
        public int Count { get; set; }
    }

    public class Cart
    {
        public List<Position> Positions { get; set; }
    }

    public class Order
    {
        public List<Figure> Positions { get; set; }

        public decimal GetTotal() =>
            Positions.Select(p => p switch
                {
                    Triangle => (decimal)p.GetArea() * 1.2m,
                    Circle => (decimal)p.GetArea() * 0.9m
                })
                .Sum();
    }

    public abstract class Figure
    {
        //убираю поля SideA, SideB, SideC т.к. ненужное дублирование полей у Square (одна сторона) и Circle (радиус)

        //по-хорошему, поменять семантику Validate, чтобы возвращал `string errorMessage` или bool TryValidate(out string errorMessage),
        //чтобы избежать оверхеда на try-catch и не мапить исключение InvalidOperationException -> BadRequestResult
        public abstract void Validate();
        public abstract double GetArea();
    }

    public sealed class Triangle : Figure
    {
        public float SideA { get; set; }
        public float SideB { get; set; }
        public float SideC { get; set; }
        
        public override void Validate()
        {
            bool CheckTriangleInequality(float a, float b, float c) => a < b + c;
            if (CheckTriangleInequality(SideA, SideB, SideC)
                && CheckTriangleInequality(SideB, SideA, SideC)
                && CheckTriangleInequality(SideC, SideB, SideA))
                return;
            throw new InvalidOperationException("Triangle restrictions not met");
        }

        public override double GetArea()
        {
            var p = (SideA + SideB + SideC) / 2;
            return Math.Sqrt(p * (p - SideA) * (p - SideB) * (p - SideC));
        }
    }

    public sealed class Square : Figure
    {
        //переименовать в `Side`, если есть возможность у потребителей перейти на новый нейминг
        public float SideA { get; set; }

        public override void Validate()
        {
            if (SideA < 0)
                throw new InvalidOperationException("Square restrictions not met");

            //Убираю проверку, зачем нам SideB если это квадрат?
        }

        public override double GetArea() => SideA * SideA;
    }

    public sealed class Circle : Figure
    {
        //переименовать в `Radius`, если есть возможность у потребителей перейти на новый нейминг
        public float SideA { get; set; }

        public override void Validate()
        {
            if (SideA < 0)
                throw new InvalidOperationException("Circle restrictions not met");
        }

        public override double GetArea() => Math.PI * SideA * SideA;
    }

    public interface IOrderStorage
    {
        // сохраняет оформленный заказ и возвращает сумму
        Task<decimal> Save(Order order);
    }

    [ApiController]
    [Route("[controller]")]
    public class FiguresController : ControllerBase
    {
        private readonly ILogger<FiguresController> _logger;
        private readonly IOrderStorage _orderStorage;
        private readonly FiguresStorage _figuresStorage;

        public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage, FiguresStorage figuresStorage)
        {
            _logger = logger;
            _orderStorage = orderStorage;
            _figuresStorage = figuresStorage;
        }

        // хотим оформить заказ и получить в ответе его стоимость
        [HttpPost]
        //использовать DTO на вход и мапить на `Cart`, можно будет спокойно менять схему бд и сущность Cart без изменения схемы API. 
        public async Task<ActionResult> Order(Cart cart, CancellationToken cancellationToken = default) //использовать токен отмены для всех async методов, если потребитель решит отменить запрос
        {
            ValidateCart(cart);

            var order = new Order
            {
                Positions = cart.Positions.Select(p =>
                {
                    //убираю Validate() т.к. валидация происходит выше
                    return p.Figure;
                }).ToList()
            };

            //Резерв и сохранение заказа вынести в сервисный (application) слой, т.к. это бизнес-логика обработки заказа. При желании, унести валидацию корзины туда же.

            //Обработка заказа (резервация и сохранение), прошедшего валидацию, должна быть реализована через очередь сообщений, которую обрабатывает отдельный Consumer,
            //исключая race conditions за резервацию позиций.

            //Нет обработки негативных сценариев если отвалился orderStorage. Если FiguresStorage недоступен, будет 500.
            //Если сделали резерв по товарам корзины, но orderStorage недоступен - что делать?
            foreach (var position in cart.Positions)
            {
                if (!_figuresStorage.TryReserve(position.Type, position.Count))
                {
                    return new BadObjectResult();
                }
            }

            var result = await _orderStorage.Save(order, cancellationToken);

            return new OkObjectResult(result.Result);
        }

        private void ValidateCart(Cart cart)
        {
            // нет проверки на null в корзине, нужен ли нам создавать заказ, у которого корзина пуста?
            foreach (var position in cart.Positions)
            {
                try
                {
                    position.Figure.Validate();
                }
                catch (InvalidOperationException e)
                {
                    return new BadRequestResult(e.Message);
                }
            }
        }
    }
}

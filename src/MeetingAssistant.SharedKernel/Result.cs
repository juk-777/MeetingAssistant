namespace MeetingAssistant.SharedKernel;

/// <summary>
/// Исход операции: успех или ожидаемый отказ с <see cref="Error"/>.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess != (error == Error.None))
        {
            throw new ArgumentException("Успех не несёт ошибки, отказ обязан её нести.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value)
        where TValue : notnull => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error)
        where TValue : notnull => new(default, false, error);
}

/// <summary>
/// Исход операции со значением; значение отказа недоступно.
/// </summary>
public sealed class Result<TValue> : Result
    where TValue : notnull
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    // Успех создаётся только из Success с non-null значением, поэтому на этой ветке _value задано.
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("У отказа нет значения.");
}

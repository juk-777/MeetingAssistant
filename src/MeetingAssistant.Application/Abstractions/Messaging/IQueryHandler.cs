using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Abstractions.Messaging;

/// <summary>
/// Обработчик запроса.
/// </summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
    where TResponse : notnull
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}

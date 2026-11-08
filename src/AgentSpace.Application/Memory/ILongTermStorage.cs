namespace AgentSpace;

public interface ILongTermStorage
{
    Task<T> TransactAsync<T>(Func<ILongTermTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

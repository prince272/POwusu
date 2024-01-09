﻿namespace POwusu.Server.Extensions.ViewRenderer
{
    public interface IViewRenderer
    {
        Task<string> RenderAsync(string name, object? model = null, CancellationToken cancellationToken = default);
    }
}
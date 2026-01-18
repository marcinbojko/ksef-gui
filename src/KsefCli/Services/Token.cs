using System;

namespace KsefCli.Services
{
    public class Token
    {
        public string? Value { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? SessionId { get; set; } // KSeF session ID associated with the token

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public bool IsValid => !string.IsNullOrEmpty(Value) && !IsExpired;
    }
}
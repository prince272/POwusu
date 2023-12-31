﻿using POwusu.Server.Extensions.Mailing;

namespace POwusu.Server.Extensions.Mailing.MailKit
{
    public class MailKitEmailOptions
    {
        public string Host { get; set; } = null!;

        public int Port { get; set; }

        public bool UseSsl { get; set; }

        public EmailAccount Account { get; set; } = null!;
    }
}
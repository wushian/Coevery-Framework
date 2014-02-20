﻿using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Coevery.Data;
using Coevery.DisplayManagement;
using Coevery.Localization;
using Coevery.Logging;
using Coevery.ContentManagement;
using Coevery.Settings;
using Coevery.Users.Models;
using Coevery.Security;
using System.Xml.Linq;
using Coevery.Services;
using System.Globalization;
using System.Text;
using Coevery.Messaging.Services;
using Coevery.Environment.Configuration;

namespace Coevery.Users.Services {
    [UsedImplicitly]
    public class UserService : IUserService {
        private static readonly TimeSpan DelayToValidate = new TimeSpan(7, 0, 0, 0); // one week to validate email
        private static readonly TimeSpan DelayToResetPassword = new TimeSpan(1, 0, 0, 0); // 24 hours to reset password

        private readonly IMembershipService _membershipService;
        private readonly IClock _clock;
        private readonly IMessageService _messageService;
        private readonly IEncryptionService _encryptionService;
        private readonly IShapeFactory _shapeFactory;
        private readonly IShapeDisplay _shapeDisplay;
        private readonly ISiteService _siteService;
        private readonly IRepository<UserRecord> _userRecordRepository;

        public UserService(
            IMembershipService membershipService, 
            IClock clock, 
            IMessageService messageService, 
            ShellSettings shellSettings, 
            IEncryptionService encryptionService,
            IShapeFactory shapeFactory,
            IShapeDisplay shapeDisplay,
            ISiteService siteService, 
            IRepository<UserRecord> userRecordRepository) {
            _membershipService = membershipService;
            _clock = clock;
            _messageService = messageService;
            _encryptionService = encryptionService;
            _shapeFactory = shapeFactory;
            _shapeDisplay = shapeDisplay;
            _siteService = siteService;
            _userRecordRepository = userRecordRepository;
            Logger = NullLogger.Instance;
        }

        public ILogger Logger { get; set; }
        public Localizer T { get; set; }

        public bool VerifyUserUnicity(string userName, string email) {
            string normalizedUserName = userName.ToLowerInvariant();

            if (_userRecordRepository.Table.Where(user => 
                                          user.NormalizedUserName == normalizedUserName || 
                                          user.Email == email)
                                   .ToList().Any()) {
                return false;
            }

            return true;
        }

        public bool VerifyUserUnicity(int id, string userName, string email) {
            string normalizedUserName = userName.ToLowerInvariant();

            if (_userRecordRepository.Table.Where(user =>
                                          user.NormalizedUserName == normalizedUserName ||
                                          user.Email == email)
                                   .ToList().Any(user => user.Id != id)) {
                return false;
            }

            return true;
        }

        public string CreateNonce(IUser user, TimeSpan delay) {
            var challengeToken = new XElement("n", new XAttribute("un", user.UserName), new XAttribute("utc", _clock.UtcNow.ToUniversalTime().Add(delay).ToString(CultureInfo.InvariantCulture))).ToString();
            var data = Encoding.UTF8.GetBytes(challengeToken);
            return Convert.ToBase64String(_encryptionService.Encode(data));
        }

        public bool DecryptNonce(string nonce, out string username, out DateTime validateByUtc) {
            username = null;
            validateByUtc = _clock.UtcNow;

            try {
                var data = _encryptionService.Decode(Convert.FromBase64String(nonce));
                var xml = Encoding.UTF8.GetString(data);
                var element = XElement.Parse(xml);
                username = element.Attribute("un").Value;
                validateByUtc = DateTime.Parse(element.Attribute("utc").Value, CultureInfo.InvariantCulture);
                return _clock.UtcNow <= validateByUtc;
            }
            catch {
                return false;
            }

        }

        public IUser ValidateChallenge(string nonce) {
            string username;
            DateTime validateByUtc;

            if (!DecryptNonce(nonce, out username, out validateByUtc)) {
                return null;
            }

            if (validateByUtc < _clock.UtcNow)
                return null;

            var user = _membershipService.GetUser(username);
            if (user == null)
                return null;

            ((UserRecord)user).EmailStatus = UserStatus.Approved;

            return user;
        }

        public void SendChallengeEmail(IUser user, Func<string, string> createUrl) {
            string nonce = CreateNonce(user, DelayToValidate);
            string url = createUrl(nonce);

            if (user != null) {
                var site = _siteService.GetSiteSettings();

                var template = _shapeFactory.Create("Template_User_Validated", Arguments.From(new {
                    RegisteredWebsite = site.As<RegistrationSettingsPart>().ValidateEmailRegisteredWebsite,
                    ContactEmail = site.As<RegistrationSettingsPart>().ValidateEmailContactEMail,
                    ChallengeUrl = url
                }));
                template.Metadata.Wrappers.Add("Template_User_Wrapper");
                
                var parameters = new Dictionary<string, object> {
                            {"Subject", T("Verification E-Mail").Text},
                            {"Body", _shapeDisplay.Display(template)},
                            {"Recipients", user.Email}
                        };

                _messageService.Send("Email", parameters);
            }
        }

        public bool SendLostPasswordEmail(string usernameOrEmail, Func<string, string> createUrl) {
            var lowerName = usernameOrEmail.ToLowerInvariant();
            var user = _userRecordRepository.Table.Where(u => u.NormalizedUserName == lowerName || u.Email == lowerName).ToList().FirstOrDefault();

            if (user != null) {
                string nonce = CreateNonce(user, DelayToResetPassword);
                string url = createUrl(nonce);

                var template = _shapeFactory.Create("Template_User_LostPassword", Arguments.From(new {
                    User = user,
                    LostPasswordUrl = url
                }));
                template.Metadata.Wrappers.Add("Template_User_Wrapper");

                var parameters = new Dictionary<string, object> {
                            {"Subject", T("Lost password").Text},
                            {"Body", _shapeDisplay.Display(template)},
                            {"Recipients", user.Email }
                        };

                _messageService.Send("Email", parameters);
                return true;
            }

            return false;
        }

        public IUser ValidateLostPassword(string nonce) {
            string username;
            DateTime validateByUtc;

            if (!DecryptNonce(nonce, out username, out validateByUtc)) {
                return null;
            }

            if (validateByUtc < _clock.UtcNow)
                return null;

            var user = _membershipService.GetUser(username);
            if (user == null)
                return null;

            return user;
        }
    }
}
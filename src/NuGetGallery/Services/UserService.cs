﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using Crypto = NuGetGallery.CryptographyService;
using NuGetGallery.Configuration;

namespace NuGetGallery
{
    public class UserService : IUserService
    {
        public IAppConfiguration Config { get; protected set; }
        public IEntityRepository<User> UserRepository { get; protected set; }

        protected UserService() {}

        public UserService(
            IAppConfiguration config,
            IEntityRepository<User> userRepository) : this()
        {
            Config = config;
            UserRepository = userRepository;
        }

        public virtual User Create(
            string username,
            string password,
            string emailAddress)
        {
            // TODO: validate input
            // TODO: consider encrypting email address with a public key, and having the background process that send messages have the private key to decrypt

            var existingUser = FindByUsername(username);
            if (existingUser != null)
            {
                throw new EntityException(Strings.UsernameNotAvailable, username);
            }

            var existingUsers = FindAllByEmailAddress(emailAddress);
            if (existingUsers.AnySafe())
            {
                throw new EntityException(Strings.EmailAddressBeingUsed, emailAddress);
            }

            var hashedPassword = Crypto.GenerateSaltedHash(password, Constants.PBKDF2HashAlgorithmId);

            var newUser = new User(username)
            {
                ApiKey = Guid.NewGuid(),
                EmailAllowed = true,
                UnconfirmedEmailAddress = emailAddress,
                EmailConfirmationToken = Crypto.GenerateToken(),
                HashedPassword = hashedPassword,
                PasswordHashAlgorithm = Constants.PBKDF2HashAlgorithmId,
                CreatedUtc = DateTime.UtcNow
            };

            if (!Config.ConfirmEmailAddresses)
            {
                newUser.ConfirmEmailAddress();
            }

            UserRepository.InsertOnCommit(newUser);
            UserRepository.CommitChanges();

            return newUser;
        }

        public void UpdateProfile(User user, bool emailAllowed)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }

            user.EmailAllowed = emailAllowed;
            UserRepository.CommitChanges();
        }

        public User FindByApiKey(Guid apiKey)
        {
            return UserRepository.GetAll().SingleOrDefault(u => u.ApiKey == apiKey);
        }

        public virtual User FindByEmailAddress(string emailAddress)
        {
            var allMatches = UserRepository.GetAll().Where(u => u.EmailAddress == emailAddress).Take(2).ToList();

            if (allMatches.Count == 1)
            {
                return allMatches[0];
            }

            return null;
        }

        public virtual IList<User> FindAllByEmailAddress(string emailAddress)
        {
            return UserRepository.GetAll().Where(u => u.EmailAddress == emailAddress).ToList();
        }

        public virtual IList<User> FindByUnconfirmedEmailAddress(string unconfirmedEmailAddress, string optionalUsername)
        {
            if (optionalUsername == null)
            {
                return UserRepository.GetAll().Where(u => u.UnconfirmedEmailAddress == unconfirmedEmailAddress).ToList();
            }
            else
            {
                return UserRepository.GetAll().Where(u => u.UnconfirmedEmailAddress == unconfirmedEmailAddress && u.Username == optionalUsername).ToList();
            }
        }

        public virtual User FindByUsername(string username)
        {
            return UserRepository.GetAll()
                .Include(u => u.Roles)
                .SingleOrDefault(u => u.Username == username);
        }

        public virtual User FindByUsernameAndPassword(string username, string password)
        {
            // TODO: validate input

            var user = FindByUsername(username);

            if (user == null)
            {
                return null;
            }

            if (!Crypto.ValidateSaltedHash(user.HashedPassword, password, user.PasswordHashAlgorithm))
            {
                return null;
            }

            return user;
        }

        public virtual User FindByUsernameOrEmailAddressAndPassword(string usernameOrEmail, string password)
        {
            // TODO: validate input

            var user = FindByUsername(usernameOrEmail)
                       ?? FindByEmailAddress(usernameOrEmail);

            if (user == null)
            {
                return null;
            }

            if (!Crypto.ValidateSaltedHash(user.HashedPassword, password, user.PasswordHashAlgorithm))
            {
                return null;
            }
            else if (!user.PasswordHashAlgorithm.Equals(Constants.PBKDF2HashAlgorithmId, StringComparison.OrdinalIgnoreCase))
            {
                // If the user can be authenticated and they are using an older password algorithm, migrate them to the current one.
                ChangePasswordInternal(user, password);
                UserRepository.CommitChanges();
            }

            return user;
        }

        public string GenerateApiKey(string username)
        {
            var user = FindByUsername(username);
            if (user == null)
            {
                return null;
            }

            var newApiKey = Guid.NewGuid();
            user.ApiKey = newApiKey;
            UserRepository.CommitChanges();
            return newApiKey.ToString();
        }

        public void ChangeEmailAddress(User user, string newEmailAddress)
        {
            var existingUsers = FindAllByEmailAddress(newEmailAddress);
            if (existingUsers.AnySafe(u => u.Key != user.Key))
            {
                throw new EntityException(Strings.EmailAddressBeingUsed, newEmailAddress);
            }

            user.UpdateEmailAddress(newEmailAddress, Crypto.GenerateToken);
            UserRepository.CommitChanges();
        }

        public bool ChangePassword(string username, string oldPassword, string newPassword)
        {
            // Review: If the old password is hashed using something other than PBKDF2, we end up making an extra db call that changes the old hash password.
            // This operation is rare enough that I'm not inclined to change it.
            var user = FindByUsernameAndPassword(username, oldPassword);
            if (user == null)
            {
                return false;
            }

            ChangePasswordInternal(user, newPassword);
            UserRepository.CommitChanges();
            return true;
        }

        public bool ConfirmEmailAddress(User user, string token)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }

            if (String.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException("token");
            }

            if (user.EmailConfirmationToken != token)
            {
                return false;
            }

            var conflictingUsers = FindAllByEmailAddress(user.UnconfirmedEmailAddress);
            if (conflictingUsers.AnySafe(u => u.Key != user.Key))
            {
                throw new EntityException(Strings.EmailAddressBeingUsed, user.UnconfirmedEmailAddress);
            }

            user.ConfirmEmailAddress();

            UserRepository.CommitChanges();
            return true;
        }

        public virtual User GeneratePasswordResetToken(string usernameOrEmail, int tokenExpirationMinutes)
        {
            if (String.IsNullOrEmpty(usernameOrEmail))
            {
                throw new ArgumentNullException("usernameOrEmail");
            }
            if (tokenExpirationMinutes < 1)
            {
                throw new ArgumentException(
                    "Token expiration should give the user at least a minute to change their password", "tokenExpirationMinutes");
            }

            var user = FindByEmailAddress(usernameOrEmail);
            if (user == null)
            {
                return null;
            }

            if (!user.Confirmed)
            {
                throw new InvalidOperationException(Strings.UserIsNotYetConfirmed);
            }

            if (!String.IsNullOrEmpty(user.PasswordResetToken) && !user.PasswordResetTokenExpirationDate.IsInThePast())
            {
                return user;
            }

            user.PasswordResetToken = Crypto.GenerateToken();
            user.PasswordResetTokenExpirationDate = DateTime.UtcNow.AddMinutes(tokenExpirationMinutes);

            UserRepository.CommitChanges();
            return user;
        }

        public bool ResetPasswordWithToken(string username, string token, string newPassword)
        {
            if (String.IsNullOrEmpty(newPassword))
            {
                throw new ArgumentNullException("newPassword");
            }

            var user = FindByUsername(username);

            if (user != null && user.PasswordResetToken == token && !user.PasswordResetTokenExpirationDate.IsInThePast())
            {
                if (!user.Confirmed)
                {
                    throw new InvalidOperationException(Strings.UserIsNotYetConfirmed);
                }

                ChangePasswordInternal(user, newPassword);
                user.PasswordResetToken = null;
                user.PasswordResetTokenExpirationDate = null;
                UserRepository.CommitChanges();
                return true;
            }

            return false;
        }

        private static void ChangePasswordInternal(User user, string newPassword)
        {
            var hashedPassword = Crypto.GenerateSaltedHash(newPassword, Constants.PBKDF2HashAlgorithmId);
            user.PasswordHashAlgorithm = Constants.PBKDF2HashAlgorithmId;
            user.HashedPassword = hashedPassword;
        }
    }
}

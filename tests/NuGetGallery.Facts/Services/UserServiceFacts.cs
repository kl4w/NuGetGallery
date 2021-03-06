﻿using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NuGetGallery.Configuration;
using Xunit;
using Xunit.Extensions;

namespace NuGetGallery
{
    public class UserServiceFacts
    {
        public static User CreateAUser(
            string username,
            string emailAddress)
        {
            return CreateAUser(username, password: null, emailAddress: emailAddress);
        }

        public static User CreateAUser(
            string username, 
            string password,
            string emailAddress,
            string hashAlgorithm = Constants.PBKDF2HashAlgorithmId)
        {
            return new User
            {
                Username = username,
                HashedPassword = String.IsNullOrEmpty(password) ? 
                    null : 
                    CryptographyService.GenerateSaltedHash(password, hashAlgorithm),
                PasswordHashAlgorithm = String.IsNullOrEmpty(password) ?
                    null :
                    hashAlgorithm,
                EmailAddress = emailAddress,
            };
        }

        public static bool VerifyPasswordHash(string hash, string algorithm, string password)
        {
            bool canAuthenticate = CryptographyService.ValidateSaltedHash(
                hash,
                password,
                algorithm);

            bool sanity = CryptographyService.ValidateSaltedHash(
                hash,
                "not_the_password",
                algorithm);

            return canAuthenticate && !sanity;
        }

        public static Credential CreatePasswordCredential(string password)
        {
            return new Credential(
                type: CredentialTypes.Password.Pbkdf2,
                value: CryptographyService.GenerateSaltedHash(
                    password, 
                    Constants.PBKDF2HashAlgorithmId));
        }

        // Now only for things that actually need a MOCK UserService object.
        private static UserService CreateMockUserService(Action<Mock<UserService>> setup, Mock<IEntityRepository<User>> userRepo = null, Mock<IAppConfiguration> config = null)
        {
            if (config == null)
            {
                config = new Mock<IAppConfiguration>();
                config.Setup(x => x.ConfirmEmailAddresses).Returns(true);
            }

            userRepo = userRepo ?? new Mock<IEntityRepository<User>>();
            var credRepo = new Mock<IEntityRepository<Credential>>();

            var userService = new Mock<UserService>(
                config.Object,
                userRepo.Object,
                credRepo.Object)
            {
                CallBase = true
            };

            if (setup != null)
            {
                setup(userService);
            }

            return userService.Object;
        }

        public class TheConfirmEmailAddressMethod
        {
            [Fact]
            public void WithTokenThatDoesNotMatchUserReturnsFalse()
            {
                var user = new User { Username = "username", EmailConfirmationToken = "token" };
                var service = new TestableUserService();

                var confirmed = service.ConfirmEmailAddress(user, "not-token");

                Assert.False(confirmed);
            }

            [Fact]
            public void ThrowsForDuplicateConfirmedEmailAddresses()
            {
                var user = new User { Username = "User1", Key = 1, EmailAddress = "old@example.org", UnconfirmedEmailAddress = "new@example.org", EmailConfirmationToken = "token" };
                var conflictingUser = new User { Username = "User2", Key = 2, EmailAddress = "new@example.org" };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new[] { user, conflictingUser }
                };

                var ex = Assert.Throws<EntityException>(() => service.ConfirmEmailAddress(user, "token"));
                Assert.Equal(String.Format(Strings.EmailAddressBeingUsed, "new@example.org"), ex.Message);
            }

            [Fact]
            public void WithTokenThatDoesMatchUserConfirmsUserAndReturnsTrue()
            {
                var user = new User
                {
                    Username = "username",
                    EmailConfirmationToken = "secret",
                    UnconfirmedEmailAddress = "new@example.com"
                };
                var service = new TestableUserService();

                var confirmed = service.ConfirmEmailAddress(user, "secret");

                Assert.True(confirmed);
                Assert.True(user.Confirmed);
                Assert.Equal("new@example.com", user.EmailAddress);
                Assert.Null(user.UnconfirmedEmailAddress);
                Assert.Null(user.EmailConfirmationToken);
            }

            [Fact]
            public void ForUserWithConfirmedEmailWithTokenThatDoesMatchUserConfirmsUserAndReturnsTrue()
            {
                var user = new User
                {
                    Username = "username",
                    EmailConfirmationToken = "secret",
                    EmailAddress = "existing@example.com",
                    UnconfirmedEmailAddress = "new@example.com"
                };
                var service = new TestableUserService();

                var confirmed = service.ConfirmEmailAddress(user, "secret");

                Assert.True(confirmed);
                Assert.True(user.Confirmed);
                Assert.Equal("new@example.com", user.EmailAddress);
                Assert.Null(user.UnconfirmedEmailAddress);
                Assert.Null(user.EmailConfirmationToken);
            }

            [Fact]
            public void WithNullUserThrowsArgumentNullException()
            {
                var service = new TestableUserService();

                Assert.Throws<ArgumentNullException>(() => service.ConfirmEmailAddress(null, "token"));
            }

            [Fact]
            public void WithEmptyTokenThrowsArgumentNullException()
            {
                var service = new TestableUserService();

                Assert.Throws<ArgumentNullException>(() => service.ConfirmEmailAddress(new User(), ""));
            }
        }

        public class TheFindByEmailAddressMethod
        {
            [Fact]
            public void ReturnsNullIfMultipleMatchesExist()
            {
                var user = new User { Username = "User1", Key = 1, EmailAddress = "new@example.org" };
                var conflictingUser = new User { Username = "User2", Key = 2, EmailAddress = "new@example.org" };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new[] { user, conflictingUser }
                };

                var result = service.FindByEmailAddress("new@example.org");
                Assert.Null(result);
            }
        }

        public class TheChangeEmailMethod
        {
            User CreateUser(string username, string password, string emailAddress)
            {
                return new User
                {
                    Username = username,
                    EmailAddress = emailAddress,
                    HashedPassword = CryptographyService.GenerateSaltedHash(password, Constants.PBKDF2HashAlgorithmId),
                    PasswordHashAlgorithm = Constants.PBKDF2HashAlgorithmId
                };
            }

            [Fact]
            public void SetsUnconfirmedEmailWhenEmailIsChanged()
            {
                var user = CreateUser("Bob", "ThePassword", "old@example.org");
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new[] { user }
                };

                service.ChangeEmailAddress(user, "new@example.org");

                Assert.Equal("old@example.org", user.EmailAddress);
                Assert.Equal("new@example.org", user.UnconfirmedEmailAddress);
                service.FakeEntitiesContext.VerifyCommitChanges();
            }

            /// <summary>
            /// It has to change the pending confirmation token whenever address changes because otherwise you can do
            /// 1. change address, get confirmation email
            /// 2. change email address again to something you don't own
            /// 3. hit confirm and you confirmed an email address you don't own
            /// </summary>
            [Fact]
            public void ModifiesConfirmationTokenWhenEmailAddressChanged()
            {
                var user = new User { EmailAddress = "old@example.com", EmailConfirmationToken = "pending-token" };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new User[] { user },
                };

                service.ChangeEmailAddress(user, "new@example.com");
                Assert.NotNull(user.EmailConfirmationToken);
                Assert.NotEmpty(user.EmailConfirmationToken);
                Assert.NotEqual("pending-token", user.EmailConfirmationToken);
                service.FakeEntitiesContext.VerifyCommitChanges();
            }

            /// <summary>
            /// It would be annoying if you start seeing pending email changes as a result of NOT changing your email address.
            /// </summary>
            [Fact]
            public void DoesNotModifyAnythingWhenConfirmedEmailAddressNotChanged()
            {
                var user = new User { EmailAddress = "old@example.com", UnconfirmedEmailAddress = null, EmailConfirmationToken = null };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new User[] { user },
                };

                service.ChangeEmailAddress(user, "old@example.com");
                Assert.True(user.Confirmed);
                Assert.Equal("old@example.com", user.EmailAddress);
                Assert.Null(user.UnconfirmedEmailAddress);
                Assert.Null(user.EmailConfirmationToken);
            }

            /// <summary>
            /// Because it's bad if your confirmation email no longer works because you did a no-op email address change.
            /// </summary>
            [Theory]
            [InlineData("something@else.com")]
            [InlineData(null)]
            public void DoesNotModifyConfirmationTokenWhenUnconfirmedEmailAddressNotChanged(string confirmedEmailAddress)
            {
                var user = new User { 
                    EmailAddress = confirmedEmailAddress,
                    UnconfirmedEmailAddress = "old@example.com", 
                    EmailConfirmationToken = "pending-token" };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new User[] { user },
                };

                service.ChangeEmailAddress(user, "old@example.com");
                Assert.Equal("pending-token", user.EmailConfirmationToken);
            }

            [Fact]
            public void DoesNotLetYouUseSomeoneElsesConfirmedEmailAddress()
            {
                var user = new User { EmailAddress = "old@example.com", Key = 1 };
                var conflictingUser = new User { EmailAddress = "new@example.com", Key = 2 };
                var service = new TestableUserServiceWithDBFaking
                {
                    Users = new User[] { user, conflictingUser },
                };

                var e = Assert.Throws<EntityException>(() => service.ChangeEmailAddress(user, "new@example.com"));
                Assert.Equal(string.Format(Strings.EmailAddressBeingUsed, "new@example.com"), e.Message);
                Assert.Equal("old@example.com", user.EmailAddress);
            }
        }

        public class TheUpdateProfileMethod
        {   
            [Fact]
            public void SavesEmailAllowedSetting()
            {
                var user = new User { EmailAddress = "old@example.org", EmailAllowed = true };
                var service = new TestableUserService();
                service.MockUserRepository
                       .Setup(r => r.GetAll())
                       .Returns(new[] { user }.AsQueryable());

                service.UpdateProfile(user, false);

                Assert.Equal(false, user.EmailAllowed);
                service.MockUserRepository
                       .Verify(r => r.CommitChanges());
            }

            [Fact]
            public void ThrowsArgumentExceptionForNullUser()
            {
                var service = new TestableUserService();

                ContractAssert.ThrowsArgNull(() => service.UpdateProfile(null, emailAllowed: true), "user");
            }
        }

        public class TestableUserService : UserService
        {
            public Mock<IAppConfiguration> MockConfig { get; protected set; }
            public Mock<IEntityRepository<User>> MockUserRepository { get; protected set; }
            public Mock<IEntityRepository<Credential>> MockCredentialRepository { get; protected set; }

            public TestableUserService()
            {
                Config = (MockConfig = new Mock<IAppConfiguration>()).Object;
                UserRepository = (MockUserRepository = new Mock<IEntityRepository<User>>()).Object;
                CredentialRepository = (MockCredentialRepository = new Mock<IEntityRepository<Credential>>()).Object;

                // Set ConfirmEmailAddress to a default of true
                MockConfig.Setup(c => c.ConfirmEmailAddresses).Returns(true);
            }
        }

        public class TestableUserServiceWithDBFaking : UserService
        {
            public Mock<IAppConfiguration> MockConfig { get; protected set; }

            public FakeEntitiesContext FakeEntitiesContext { get; set; }
            
            public IEnumerable<User> Users
            {
                set
                {
                    foreach (User u in value) FakeEntitiesContext.Set<User>().Add(u);
                }
            }
            
            public TestableUserServiceWithDBFaking()
            {
                Config = (MockConfig = new Mock<IAppConfiguration>()).Object;
                UserRepository = new EntityRepository<User>(FakeEntitiesContext = new FakeEntitiesContext());
            }
        }
    }
}


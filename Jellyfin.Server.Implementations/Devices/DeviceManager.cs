using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Dtos;
using Jellyfin.Data.Events;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Devices;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Devices
{
    /// <summary>
    /// Manages the creation, updating, and retrieval of devices.
    /// </summary>
    public class DeviceManager : IDeviceManager
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
        private readonly IUserManager _userManager;
        private readonly ConcurrentDictionary<string, ClientCapabilities> _capabilitiesMap = new();
        private readonly ConcurrentDictionary<int, Device> _devices;
        private readonly ConcurrentDictionary<string, DeviceOptions> _deviceOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        /// <param name="userManager">The user manager.</param>
        public DeviceManager(IDbContextFactory<JellyfinDbContext> dbProvider, IUserManager userManager)
        {
            _dbProvider = dbProvider;
            _userManager = userManager;
            _devices = new ConcurrentDictionary<int, Device>();
            _deviceOptions = new ConcurrentDictionary<string, DeviceOptions>();

            using var dbContext = _dbProvider.CreateDbContext();
            foreach (var device in dbContext.Devices
                         .OrderBy(d => d.Id)
                         .AsEnumerable())
            {
                _devices.TryAdd(device.Id, device);
            }

            foreach (var deviceOption in dbContext.DeviceOptions
                         .OrderBy(d => d.Id)
                         .AsEnumerable())
            {
                _deviceOptions.TryAdd(deviceOption.DeviceId, deviceOption);
            }
        }

        /// <inheritdoc />
        public event EventHandler<GenericEventArgs<Tuple<string, DeviceOptions>>>? DeviceOptionsUpdated;

        /// <inheritdoc />
        public void SaveCapabilities(string deviceId, ClientCapabilities capabilities)
        {
            _capabilitiesMap[deviceId] = capabilities;

            var json = JsonSerializer.Serialize(capabilities, JsonDefaults.Options);
            var devices = GetDevicesToUpdate(deviceId, d => d.Capabilities, json);
            if (devices.Count == 0)
            {
                return;
            }

            using var dbContext = _dbProvider.CreateDbContext();
            dbContext.Devices
                .Where(d => d.DeviceId == deviceId)
                .ExecuteUpdate(s => s.SetProperty(d => d.Capabilities, json));

            foreach (var device in devices)
            {
                device.Capabilities = json;
            }
        }

        /// <inheritdoc />
        public async Task SavePlaybackDeviceProfile(string deviceId, DeviceProfile profile)
        {
            var json = JsonSerializer.Serialize(profile, JsonDefaults.Options);
            var devices = GetDevicesToUpdate(deviceId, d => d.PlaybackDeviceProfile, json);
            if (devices.Count == 0)
            {
                return;
            }

            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                await dbContext.Devices
                    .Where(d => d.DeviceId == deviceId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.PlaybackDeviceProfile, json))
                    .ConfigureAwait(false);
            }

            foreach (var device in devices)
            {
                device.PlaybackDeviceProfile = json;
            }
        }

        /// <inheritdoc />
        public DeviceCapabilitiesDto? GetStoredCapabilities(string deviceId)
        {
            var devices = _devices.Values
                .Where(d => d.DeviceId == deviceId)
                .OrderByDescending(d => d.DateLastActivity)
                .ToList();
            if (devices.Count == 0)
            {
                return null;
            }

            return new()
            {
                DeviceId = deviceId,
                Capabilities = devices.Select(d => d.Capabilities).FirstOrDefault(c => c is not null),
                PlaybackDeviceProfile = devices.Select(d => d.PlaybackDeviceProfile).FirstOrDefault(p => p is not null)
            };
        }

        /// <inheritdoc />
        public async Task UpdateDeviceOptions(string deviceId, string? deviceName)
        {
            DeviceOptions? deviceOptions;
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                deviceOptions = await dbContext.DeviceOptions.FirstOrDefaultAsync(dev => dev.DeviceId == deviceId).ConfigureAwait(false);
                if (deviceOptions is null)
                {
                    deviceOptions = new DeviceOptions(deviceId);
                    dbContext.DeviceOptions.Add(deviceOptions);
                }

                deviceOptions.CustomName = deviceName;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            _deviceOptions[deviceId] = deviceOptions;

            DeviceOptionsUpdated?.Invoke(this, new GenericEventArgs<Tuple<string, DeviceOptions>>(new Tuple<string, DeviceOptions>(deviceId, deviceOptions)));
        }

        /// <inheritdoc />
        public async Task<Device> CreateDevice(Device device)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                dbContext.Devices.Add(device);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                _devices.TryAdd(device.Id, device);
            }

            return device;
        }

        /// <inheritdoc />
        public DeviceOptionsDto? GetDeviceOptions(string deviceId)
        {
            if (_deviceOptions.TryGetValue(deviceId, out var deviceOptions))
            {
                return ToDeviceOptionsDto(deviceOptions);
            }

            return null;
        }

        /// <inheritdoc />
        public ClientCapabilities GetCapabilities(string? deviceId)
        {
            if (deviceId is null)
            {
                return new();
            }

            return _capabilitiesMap.TryGetValue(deviceId, out ClientCapabilities? result)
                ? result
                : new();
        }

        /// <inheritdoc />
        public DeviceInfoDto? GetDevice(string id)
        {
            var device = _devices.Values.Where(d => d.DeviceId == id).OrderByDescending(d => d.DateLastActivity).FirstOrDefault();
            _deviceOptions.TryGetValue(id, out var deviceOption);

            var deviceInfo = device is null ? null : ToDeviceInfo(device, deviceOption);
            return deviceInfo is null ? null : ToDeviceInfoDto(deviceInfo);
        }

        /// <inheritdoc />
        public QueryResult<Device> GetDevices(DeviceQuery query)
        {
            IEnumerable<Device> devices = _devices.Values
                .Where(device => !query.UserId.HasValue || device.UserId.Equals(query.UserId.Value))
                .Where(device => query.DeviceId is null || device.DeviceId == query.DeviceId)
                .Where(device => query.AccessToken is null || device.AccessToken == query.AccessToken)
                .OrderBy(d => d.Id)
                .ToList();
            var count = devices.Count();

            if (query.Skip.HasValue)
            {
                devices = devices.Skip(query.Skip.Value);
            }

            if (query.Limit.HasValue && query.Limit.Value > 0)
            {
                devices = devices.Take(query.Limit.Value);
            }

            return new QueryResult<Device>(query.Skip, count, devices.ToList());
        }

        /// <inheritdoc />
        public QueryResult<DeviceInfo> GetDeviceInfos(DeviceQuery query)
        {
            var devices = GetDevices(query);

            return new QueryResult<DeviceInfo>(
                devices.StartIndex,
                devices.TotalRecordCount,
                devices.Items.Select(device => ToDeviceInfo(device)).ToList());
        }

        /// <inheritdoc />
        public QueryResult<DeviceInfoDto> GetDevicesForUser(Guid? userId)
        {
            IEnumerable<Device> devices = _devices.Values
                .OrderByDescending(d => d.DateLastActivity)
                .ThenBy(d => d.DeviceId);

            if (!userId.IsNullOrEmpty())
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user is null)
                {
                    throw new ResourceNotFoundException();
                }

                devices = devices.Where(i => CanAccessDevice(user, i.DeviceId));
            }

            var array = devices.Select(device =>
                {
                    _deviceOptions.TryGetValue(device.DeviceId, out var option);
                    return ToDeviceInfo(device, option);
                })
                .Select(ToDeviceInfoDto)
                .ToArray();

            return new QueryResult<DeviceInfoDto>(array);
        }

        /// <inheritdoc />
        public async Task DeleteDevice(Device device)
        {
            _devices.TryRemove(device.Id, out _);
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                await dbContext.Devices
                    .Where(d => d.Id == device.Id)
                    .ExecuteDeleteAsync()
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task UpdateDevice(Device device)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                dbContext.Devices.Update(device);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            _devices[device.Id] = device;
        }

        /// <inheritdoc />
        public bool CanAccessDevice(User user, string deviceId)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentException.ThrowIfNullOrEmpty(deviceId);

            if (user.HasPermission(PermissionKind.EnableAllDevices) || user.HasPermission(PermissionKind.IsAdministrator))
            {
                return true;
            }

            return user.GetPreference(PreferenceKind.EnabledDevices).Contains(deviceId, StringComparison.OrdinalIgnoreCase)
                   || !GetCapabilities(deviceId).SupportsPersistentIdentifier;
        }

        // Only cached devices whose stored value differs need a database write.
        private List<Device> GetDevicesToUpdate(string deviceId, Func<Device, string?> getValue, string value)
            => _devices.Values
                .Where(d => d.DeviceId == deviceId && !string.Equals(getValue(d), value, StringComparison.Ordinal))
                .ToList();

        private DeviceInfo ToDeviceInfo(Device authInfo, DeviceOptions? options = null)
        {
            var caps = GetCapabilities(authInfo.DeviceId);
            var user = _userManager.GetUserById(authInfo.UserId) ?? throw new ResourceNotFoundException("User with UserId " + authInfo.UserId + " not found");

            return new()
            {
                AppName = authInfo.AppName,
                AppVersion = authInfo.AppVersion,
                Id = authInfo.DeviceId,
                LastUserId = authInfo.UserId,
                LastUserName = user.Username,
                Name = authInfo.DeviceName,
                DateLastActivity = authInfo.DateLastActivity,
                IconUrl = caps.IconUrl,
                CustomName = options?.CustomName,
            };
        }

        private DeviceOptionsDto ToDeviceOptionsDto(DeviceOptions options)
        {
            return new()
            {
                Id = options.Id,
                DeviceId = options.DeviceId,
                CustomName = options.CustomName,
            };
        }

        private DeviceInfoDto ToDeviceInfoDto(DeviceInfo info)
        {
            return new()
            {
                Name = info.Name,
                CustomName = info.CustomName,
                AccessToken = info.AccessToken,
                Id = info.Id,
                LastUserName = info.LastUserName,
                AppName = info.AppName,
                AppVersion = info.AppVersion,
                LastUserId = info.LastUserId,
                DateLastActivity = info.DateLastActivity,
                Capabilities = ToClientCapabilitiesDto(info.Capabilities),
                IconUrl = info.IconUrl
            };
        }

        /// <inheritdoc />
        public ClientCapabilitiesDto ToClientCapabilitiesDto(ClientCapabilities capabilities)
        {
            return new()
            {
                PlayableMediaTypes = capabilities.PlayableMediaTypes,
                SupportedCommands = capabilities.SupportedCommands,
                SupportsMediaControl = capabilities.SupportsMediaControl,
                SupportsPersistentIdentifier = capabilities.SupportsPersistentIdentifier,
                DeviceProfile = capabilities.DeviceProfile,
                AppStoreUrl = capabilities.AppStoreUrl,
                IconUrl = capabilities.IconUrl
            };
        }
    }
}

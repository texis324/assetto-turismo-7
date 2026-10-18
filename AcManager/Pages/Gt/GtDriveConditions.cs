using System;
using System.Collections.Generic;
using AcManager.Tools.GameProperties;
using AcManager.Tools.Profile;
using AcTools.Processes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcManager.Pages.Gt {
    /// <summary>One time-trial preparation session's launch conditions, retained across car changes.</summary>
    internal sealed class GtDriveConditions {
        private bool _captured;
        private string _preset;
        private Game.ConditionProperties _conditions;
        private Game.TrackProperties _track;
        private WeatherDetails _weather;
        private WeatherSpecificDate _date;
        private int _weatherIndex, _dateIndex;

        internal GtDriveConditions(string preset) {
            _preset = preset;
        }

        internal string Preset { get { return _preset; } }

        internal void Apply(Game.StartProperties properties) {
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            if (!_captured) {
                if (properties.ConditionProperties == null) {
                    throw new InvalidOperationException("Time-trial launch conditions are missing.");
                }
                var conditions = Copy(properties.ConditionProperties);
                var weather = properties.GetAdditional<WeatherDetails>();
                var date = properties.GetAdditional<WeatherSpecificDate>();
                var launchPreset = properties.GetAdditional<QuickDrivePresetProperty>()?.SerializedData;
                // Normalize the actual exported launch preset: ideal conditions can have changed
                // track grip, and random/live weather has already been resolved by QuickDrive.Go.
                var preset = NormalizePreset(string.IsNullOrWhiteSpace(launchPreset) ? _preset : launchPreset, conditions, date);
                _conditions = conditions;
                _track = Copy(properties.TrackProperties);
                // These two metadata objects are not disposable launch helpers. WeatherDescription
                // and the date are immutable, and WeatherDetails retains the selected CSP controller.
                _weather = weather;
                _date = date == null ? null : new WeatherSpecificDate(date.UseSpecificDate, date.Date);
                _weatherIndex = FindAdditional<WeatherDetails>(properties.AdditionalPropertieses);
                _dateIndex = FindAdditional<WeatherSpecificDate>(properties.AdditionalPropertieses);
                _preset = preset;
                _captured = true;
                return;
            }

            properties.ConditionProperties = Copy(_conditions);
            properties.TrackProperties = Copy(_track);
            // Keep metadata ahead of the existing weather scripts, retaining their original order.
            // If a later launch omits both items, insert the earlier original item first so
            // restoring it does not shift the second item past a disposable weather helper.
            var dateCopy = _date == null ? null : new WeatherSpecificDate(_date.UseSpecificDate, _date.Date);
            if (_dateIndex < _weatherIndex) {
                ReplaceAdditional(properties.AdditionalPropertieses, dateCopy, _dateIndex);
                ReplaceAdditional(properties.AdditionalPropertieses, _weather, _weatherIndex);
            } else {
                ReplaceAdditional(properties.AdditionalPropertieses, _weather, _weatherIndex);
                ReplaceAdditional(properties.AdditionalPropertieses, dateCopy, _dateIndex);
            }
        }

        private static string NormalizePreset(string preset, Game.ConditionProperties conditions, WeatherSpecificDate date) {
            var value = string.IsNullOrWhiteSpace(preset) ? new JObject() : JObject.Parse(preset);
            value["RealConditions"] = false;
            value["ico"] = false;
            value["rws"] = false;
            value["rwd"] = false;
            value["rte"] = false;
            value["rti"] = false;
            value["WeatherId"] = conditions.WeatherName;
            if (conditions.AmbientTemperature.HasValue) value["Temperature"] = conditions.AmbientTemperature.Value;
            if (conditions.SunAngle.HasValue) value["Time"] = (int)Math.Round(Game.ConditionProperties.GetSeconds(conditions.SunAngle.Value));
            // Preserve the historic QuickDrive JSON spelling and aliases.
            // QuickDrive.SaveableData stores an int. Writing the ConditionProperties double
            // directly produces e.g. 1.0, which its JSON integer reader rejects.
            if (conditions.TimeMultiplier.HasValue) value["TimeMultipler"] = Convert.ToInt32(conditions.TimeMultiplier.Value);
            if (conditions.WindSpeedMin.HasValue) value["wsf"] = conditions.WindSpeedMin.Value;
            if (conditions.WindSpeedMax.HasValue) value["wst"] = conditions.WindSpeedMax.Value;
            if (conditions.WindDirectionDeg.HasValue) value["wd"] = conditions.WindDirectionDeg.Value;
            value["crt"] = conditions.RoadTemperature.HasValue;
            value["crtv"] = conditions.RoadTemperature.HasValue ? new JValue(conditions.RoadTemperature.Value) : JValue.CreateNull();
            if (date != null) {
                value["udt"] = date.UseSpecificDate;
                value["dtv"] = date.Date;
            }
            return value.ToString(Formatting.None);
        }

        private static Game.ConditionProperties Copy(Game.ConditionProperties source) {
            return source == null ? null : new Game.ConditionProperties {
                SunAngle = source.SunAngle, TimeMultiplier = source.TimeMultiplier, CloudSpeed = source.CloudSpeed,
                RoadTemperature = source.RoadTemperature, AmbientTemperature = source.AmbientTemperature,
                WindDirectionDeg = source.WindDirectionDeg, WindSpeedMin = source.WindSpeedMin, WindSpeedMax = source.WindSpeedMax,
                WeatherName = source.WeatherName
            };
        }

        private static Game.TrackProperties Copy(Game.TrackProperties source) {
            return source == null ? null : new Game.TrackProperties {
                Preset = source.Preset, SessionStart = source.SessionStart, Randomness = source.Randomness,
                LapGain = source.LapGain, SessionTransfer = source.SessionTransfer
            };
        }

        private static int FindAdditional<T>(IList<object> items) where T : class {
            for (var i = 0; i < items.Count; ++i) if (items[i] is T) return i;
            return -1;
        }

        private static void ReplaceAdditional<T>(IList<object> items, T retained, int originalIndex) where T : class {
            var index = FindAdditional<T>(items);
            if (index >= 0) {
                if (retained == null) items.RemoveAt(index);
                else items[index] = retained;
            } else if (retained != null) {
                items.Insert(Math.Min(Math.Max(originalIndex, 0), items.Count), retained);
            }
        }
    }
}

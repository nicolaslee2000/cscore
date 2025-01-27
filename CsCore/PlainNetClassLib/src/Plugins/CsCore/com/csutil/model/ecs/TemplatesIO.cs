﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using com.csutil.json;
using JsonDiffPatchDotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Zio;

namespace com.csutil.model.ecs {

    /// <summary> A system to put entities into and to load/save them to a specified DirectoryEntry which unlocks using them as
    /// templates for other entities. An in-memory DirectoryEntry can be used if saving to disk is not desired </summary>
    public class TemplatesIO<T> : IDisposableV2 where T : IEntityData {

        public DisposeState IsDisposed { get; private set; } = DisposeState.Active;

        private readonly DirectoryEntry EntityDir;
        private readonly JsonDiffPatch JonDiffPatch = new JsonDiffPatch();

        /// <summary> A cache of all loaded templates and variants,
        /// these need to be combined with all parent entities to get the full entity data </summary>
        private readonly Dictionary<string, JToken> EntityCache = new Dictionary<string, JToken>();

        private Func<JsonSerializer> GetJsonSerializer = () => JsonSerializer.Create(JsonNetSettings.typedJsonSettings);

        public TemplatesIO(DirectoryEntry entityDir) {
            EntityDir = entityDir;
        }

        public void Dispose() {
            IsDisposed = DisposeState.DisposingStarted;
            EntityCache.Clear();
            GetJsonSerializer = null;
            IsDisposed = DisposeState.Disposed;
        }

        /// <summary> Loads all template files from disk into memory </summary>
        public async Task LoadAllTemplateFilesIntoMemory() {
            this.ThrowErrorIfDisposed();
            var jsonSerializer = GetJsonSerializer();
            var tasks = new List<Task>();
            foreach (var templateFile in EntityDir.EnumerateFiles()) {
                tasks.Add(TaskV2.Run((() => LoadJTokenFromFile(templateFile, jsonSerializer))));
            }
            await Task.WhenAll(tasks);
        }

        private void LoadJTokenFromFile(FileEntry templateFile, JsonSerializer jsonSerializer) {
            var templateId = templateFile.Name;
            if (EntityCache.ContainsKey(templateId)) { return; }
            using (var stream = templateFile.OpenForRead()) {
                JToken template = jsonSerializer.Deserialize<JToken>(new JsonTextReader(new StreamReader(stream)));
                UpdateEntitiesCache(templateId, template);
            }
        }

        public void SaveChanges(T instance) {
            var entityId = instance.GetId();
            var json = UpdateJsonState(instance);
            var file = GetEntityFileForId(entityId);
            file.SaveAsJson(json);
        }

        private JToken UpdateJsonState(T entity) {
            var json = ToJToken(entity, GetJsonSerializer());
            var templateId = entity.TemplateId;
            if (templateId != null) {
                var template = ComposeFullJson(templateId, allowLazyLoadFromDisk: true);
                json = JonDiffPatch.Diff(template, json);
            }
            UpdateEntitiesCache(entity.GetId(), json);
            return json;
        }

        private void UpdateEntitiesCache(string id, JToken entity) {
            lock (EntityCache) {
                EntityCache[id] = entity;
            }
        }

        private FileEntry GetEntityFileForId(string entityId) {
            entityId.ThrowErrorIfNullOrEmpty("entityId");
            return EntityDir.GetChild(entityId);
        }

        public void Delete(string entityId) {
            if (EntityCache.Remove(entityId)) {
                var entityFile = GetEntityFileForId(entityId);
                if (entityFile.Exists) {
                    entityFile.DeleteV2();
                }
            }
        }

        private JToken ToJToken(T instance, JsonSerializer serializer) {
            return JToken.FromObject(instance, serializer);
        }

        /// <summary> Creates a variant instance of the given entity, the parent of the new variant will be defined by the
        /// passed in dictionary, for the top level entity of the variant subtree typically no new parent id is passed in
        /// so that the parent will be null and with that the new variant overall will not automatically be attached to
        /// the same parent as the template. </summary>
        /// <param name="newIdsLookup"> Requires to pass in a filled dictionary with the current entity ids
        /// mapping to new ones that will be used for the new instances </param>
        public T CreateVariantInstanceOf(T entity, Dictionary<string, string> newIdsLookup) {
            var templateId = entity.GetId();
            if (!IsSavedToFiles(templateId)) {
                throw new InvalidOperationException($"The passed entity {entity} first needs to be saved once before it can be used as a template");
            }
            JsonSerializer serializer = GetJsonSerializer();
            var variantJson = ToJToken(entity, serializer);
            { // Update the entity jtoken to become a variant jtoken:
                variantJson["Id"] = newIdsLookup[entity.Id];
                variantJson["TemplateId"] = templateId;
                if (entity.ParentId != null && newIdsLookup.TryGetValue(entity.ParentId, out var newParentId)) {
                    variantJson["ParentId"] = newParentId;
                } else {
                    variantJson["ParentId"] = null;
                }
                if (!entity.ChildrenIds.IsNullOrEmpty()) {
                    var newChildrenIds = entity.ChildrenIds.Map(x => newIdsLookup[x]);
                    var newChildrenIdsJArray = (JArray)JToken.FromObject(newChildrenIds, serializer);
                    var oldChildrenIds = (JObject)variantJson["ChildrenIds"];
                    oldChildrenIds.Property("$values").Value = newChildrenIdsJArray;
                }
            }
            return ToObject(variantJson, serializer);
        }

        private bool IsSavedToFiles(string entityId) {
            return GetEntityFileForId(entityId).Exists;
        }

        private T ToObject(JToken json, JsonSerializer serializer) {
            T entity = json.ToObject<T>(serializer);
            AssertAllFieldsWereDeserialized(json, entity);
            return entity;
        }

        [Conditional("DEBUG")]
        private void AssertAllFieldsWereDeserialized(JToken sourceJson, T resultingEntity) {
            var backAsJson = ToJToken(resultingEntity, GetJsonSerializer());
            var diff = JonDiffPatch.Diff(sourceJson, backAsJson);
            if (diff != null) {
                throw new Exception($"Not all props of {typeof(T)} were deserialized, diff:" + diff);
            }
        }

        public IEnumerable<string> GetAllEntityIds() {
            return EntityDir.EnumerateFiles().Map(x => x.Name);
        }

        /// <summary> Composes an entity instance based on the involved templates </summary>
        /// <param name="entityId"> The id of the entity to compose </param>
        /// <param name="allowLazyLoadFromDisk"> if false its is expected all involved templates were
        /// already loaded into memory via <see cref="LoadAllTemplateFilesIntoMemory"/> </param>
        public T ComposeEntityInstance(string entityId, bool allowLazyLoadFromDisk = true) {
            return ToObject(ComposeFullJson(entityId, allowLazyLoadFromDisk), GetJsonSerializer());
        }

        /// <summary> Recursively composes the full json for the given entity id by applying the templates </summary>
        private JToken ComposeFullJson(string entityId, bool allowLazyLoadFromDisk) {
            if (!EntityCache.ContainsKey(entityId)) {
                if (allowLazyLoadFromDisk) {
                    var entityFile = GetEntityFileForId(entityId);
                    if (!entityFile.Exists) {
                        throw new KeyNotFoundException("Entity never stored to disk: " + entityId);
                    }
                    LoadJTokenFromFile(entityFile, GetJsonSerializer());
                } else {
                    throw new KeyNotFoundException("Entity not found: " + entityId);
                }
            }
            var json = EntityCache[entityId];
            if (json["TemplateId"] is JArray templateIdArray) {
                var templateId = templateIdArray[1].Value<string>();
                var template = ComposeFullJson(templateId, allowLazyLoadFromDisk);
                json = JonDiffPatch.Patch(template, json);
            }
            return json;
        }

        public T RecreateVariantInstance(string entityId) {
            return ToObject(ComposeFullJsonOnlyFromMemory(entityId), GetJsonSerializer());
        }

        private JToken ComposeFullJsonOnlyFromMemory(string entityId) {
            if (!EntityCache.ContainsKey(entityId)) {
                throw new KeyNotFoundException("Entity not found in memory: " + entityId);
            }
            var json = EntityCache[entityId];
            if (json["TemplateId"] is JArray templateIdArray) {
                var templateId = templateIdArray[1].Value<string>();
                var template = ComposeFullJsonOnlyFromMemory(templateId);
                json = JonDiffPatch.Patch(template, json);
            }
            return json;
        }

        public bool HasChanges(T oldState, T newState) {
            var s = GetJsonSerializer();
            var diff = JonDiffPatch.Diff(ToJToken(oldState, s), ToJToken(newState, s));
            return diff != null;
        }

    }

}
﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace com.csutil.model.mtvmtv {

    public static class ViewModelExtensions {

        public static JTokenType GetJTokenType(this ViewModel self) {
            return EnumUtil.Parse<JTokenType>(self.type.ToFirstCharUpperCase());
        }

        public static string ToJsonSchemaType(this JTokenType self) {
            return ("" + self).ToFirstCharLowerCase();
        }

        public static JValue NewDefaultJValue(this ViewModel self) {
            return self.ParseToJValue(self.defaultVal);
        }

        public static JValue ParseToJValue(this ViewModel self, string newVal) {
            switch (self.GetJTokenType()) {
                case JTokenType.Boolean:
                    if (newVal == null) { return new JValue(false); }
                    return new JValue(bool.Parse(newVal));
                case JTokenType.Integer:
                    if (newVal == null) { return new JValue(0); }
                    return new JValue(int.Parse(newVal));
                case JTokenType.Float:
                    if (newVal == null) { return new JValue(0f); }
                    return new JValue(float.Parse(newVal));
                case JTokenType.String:
                    if (newVal == null) { return new JValue(""); }
                    return new JValue(newVal);
            }
            throw new NotImplementedException("Cant handle type " + self.type);
        }

        public static IEnumerable<string> GetOrder(this ViewModel self) {
            return self.order != null ? self.order : self.properties.Map(x => x.Key);
        }

    }

}
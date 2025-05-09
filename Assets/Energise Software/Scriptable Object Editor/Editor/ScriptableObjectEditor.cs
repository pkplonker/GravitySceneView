using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine.Profiling;

namespace ScriptableObjectEditor
{
	public partial class ScriptableObjectEditorWindow : EditorWindow
	{
		private Vector2 scrollPosition;
		private List<Type> scriptableObjectTypes;
		private string[] typeNames;
		[SerializeField]
		private int selectedTypeIndex;
		[SerializeField]
		private int selectedAssemblyIndex;

		private List<ScriptableObject> currentTypeObjects = new();
		private static string assetsFolderPath = "Assets";

		private List<Assembly> availableAssemblies;
		private string[] assemblyNames;
		
		private bool includeDerivedTypes = true;
		private int draggingColumn = -1;
		private float dragStartMouseX;
		private float dragStartWidth;
		private List<float> columnWidths = new();
		private string typeSearchString = string.Empty;
		private string instanceSearchString = string.Empty;
		private int sortColumnIndex = -1;
		private bool sortAscending = true;
		private Dictionary<ScriptableObject, long> memoryUsage = new();
		private long totalMemoryAll;
		private long totalMemoryFiltered;
		private Dictionary<string, bool> regions = new();
		private int createCount;

		[MenuItem("Window/Energise Tools/Scriptable Object Editor &%S")]
		public static void ShowWindow() => GetWindow<ScriptableObjectEditorWindow>("Scriptable Object Editor");

		static ScriptableObjectEditorWindow()
		{
			AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
		}

		private static void OnAfterAssemblyReload()
		{
			var window = GetWindow<ScriptableObjectEditorWindow>();
			window.LoadAvailableAssemblies();
			window.LoadScriptableObjectTypes();
			window.LoadObjectsOfType(window.scriptableObjectTypes[window.selectedTypeIndex]);
			window.Repaint();
		}

		private void OnEnable()
		{
			LoadAvailableAssemblies();
			LoadScriptableObjectTypes();
			LoadObjectsOfType(scriptableObjectTypes.FirstOrDefault());
		}

		private void LoadAvailableAssemblies()
		{
			availableAssemblies = GetAssembliesWithScriptableObjects();
			assemblyNames = availableAssemblies
				.Select(assembly => assembly.GetName().Name)
				.Prepend("All Assemblies")
				.ToArray();
		}

		private static List<Assembly> GetAssembliesWithScriptableObjects()
		{
			return AppDomain.CurrentDomain.GetAssemblies()
				.Where(assembly =>
					!assembly.FullName.StartsWith("UnityEngine") &&
					!assembly.FullName.StartsWith("UnityEditor") &&
					!assembly.FullName.StartsWith("Unity."))
				.Where(assembly =>
					assembly.GetTypes().Any(type =>
						type.IsSubclassOf(typeof(ScriptableObject)) && !type.IsAbstract && IsInAssetsFolder(type)))
				.OrderBy(assembly => assembly.GetName().Name)
				.ToList();
		}

		private bool GetExpandedRegion(string key)
		{
			if (regions.ContainsKey(key))
			{
				return regions[key];
			}

			regions.Add(key, false);
			return false;
		}

		private void LoadScriptableObjectTypes()
		{
			IEnumerable<Type> types = selectedAssemblyIndex == 0
				? availableAssemblies.SelectMany(a => a.GetTypes())
				: availableAssemblies[selectedAssemblyIndex - 1].GetTypes();

			scriptableObjectTypes = types
				.Where(t => t.IsSubclassOf(typeof(ScriptableObject)) && !t.IsAbstract && IsInAssetsFolder(t))
				.OrderBy(t => t.Name)
				.ToList();

			if (!string.IsNullOrEmpty(typeSearchString))
			{
				scriptableObjectTypes = scriptableObjectTypes
					.Where(t => t.Name.IndexOf(typeSearchString, StringComparison.OrdinalIgnoreCase) >= 0)
					.ToList();
			}

			typeNames = scriptableObjectTypes.Select(t => t.Name).ToArray();
			selectedTypeIndex = Mathf.Clamp(selectedTypeIndex, 0, typeNames.Length - 1);
		}

		private static bool IsInAssetsFolder(Type type)
		{
			var guids = AssetDatabase.FindAssets($"t:{type.Name}", new[] {assetsFolderPath});
			return guids.Any();
		}

		private void LoadObjectsOfType(Type type)
		{
			currentTypeObjects.Clear();
			memoryUsage.Clear();
			totalMemoryAll = 0;
			totalMemoryFiltered = 0;
			if (type == null) return;

			var all = new List<ScriptableObject>();
			var guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] {assetsFolderPath});
			foreach (var guid in guids)
			{
				var path = AssetDatabase.GUIDToAssetPath(guid);
				var obj = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
				if (obj == null) continue;
				if ((includeDerivedTypes && type.IsAssignableFrom(obj.GetType())) ||
				    (!includeDerivedTypes && obj.GetType() == type))
				{
					all.Add(obj);
				}
			}

			foreach (var obj in all)
			{
				long mem = Profiler.GetRuntimeMemorySizeLong(obj);
				memoryUsage[obj] = mem;
				totalMemoryAll += mem;
			}

			currentTypeObjects = string.IsNullOrEmpty(instanceSearchString)
				? all
				: all.Where(o => o.name.IndexOf(instanceSearchString, StringComparison.OrdinalIgnoreCase) >= 0)
					.ToList();

			foreach (var obj in currentTypeObjects)
				totalMemoryFiltered += memoryUsage[obj];

			ApplySorting();
		}

		private void OnGUI()
		{
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			EditorGUILayout.BeginVertical("box");

			EditorGUILayout.LabelField("Asset Management", EditorStyles.boldLabel);
			var expand = Fold("Assemblies");
			if (expand)
			{
				EditorGUILayout.BeginHorizontal();

				EditorGUILayout.LabelField("Path", GUILayout.Width(40));
				assetsFolderPath = EditorGUILayout.TextField(assetsFolderPath, GUILayout.Width(200));
				if (GUILayout.Button("Browse", GUILayout.Width(80)))
				{
					string sel = EditorUtility.OpenFolderPanel("Select Scriptable Object Folder", assetsFolderPath, "");
					if (!string.IsNullOrEmpty(sel))
					{
						assetsFolderPath = sel.Replace(Application.dataPath, "Assets");
						LoadScriptableObjectTypes();
						LoadObjectsOfType(scriptableObjectTypes.FirstOrDefault());
					}
				}

				if (GUILayout.Button(EditorGUIUtility.IconContent("d_Refresh"), GUILayout.Width(35)))
				{
					LoadAvailableAssemblies();
					LoadScriptableObjectTypes();
					LoadObjectsOfType(scriptableObjectTypes.FirstOrDefault());
				}

				int newAsm = EditorGUILayout.Popup(selectedAssemblyIndex, assemblyNames, GUILayout.Width(200));
				if (newAsm != selectedAssemblyIndex)
				{
					selectedAssemblyIndex = newAsm;
					LoadScriptableObjectTypes();
					LoadObjectsOfType(scriptableObjectTypes.FirstOrDefault());
				}

				EditorGUILayout.EndHorizontal();
			}

			EditorGUILayout.Space();

			var expanded = Fold("Stats");
			if (expanded)
			{
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField($"Count: {currentTypeObjects.Count}", GUILayout.Width(100));
				EditorGUILayout.LabelField($"Total Memory: {totalMemoryAll / 1024f:F1} KB", GUILayout.Width(140));
				EditorGUILayout.LabelField($"Filtered Memory: {totalMemoryFiltered / 1024f:F1} KB",
					GUILayout.Width(160));
				GUILayout.FlexibleSpace();

				EditorGUILayout.EndHorizontal();
			}

			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField("Type", GUILayout.Width(40));
			int newTypeIdx = EditorGUILayout.Popup(selectedTypeIndex, typeNames, GUILayout.Width(200));
			if (newTypeIdx != selectedTypeIndex)
			{
				selectedTypeIndex = newTypeIdx;
				LoadObjectsOfType(scriptableObjectTypes[selectedTypeIndex]);
			}

			includeDerivedTypes =
				EditorGUILayout.ToggleLeft("Include Derived", includeDerivedTypes, GUILayout.Width(120));
			EditorGUILayout.LabelField("Search Types", GUILayout.Width(80));

			var newTypeSearch = EditorGUILayout.TextField(typeSearchString, GUILayout.Width(200));
			if (newTypeSearch != typeSearchString)
			{
				typeSearchString = newTypeSearch;
				LoadScriptableObjectTypes();
				LoadObjectsOfType(scriptableObjectTypes.FirstOrDefault());
			}

			EditorGUILayout.LabelField("Search Instances", GUILayout.Width(100));
			var newInstSearch = EditorGUILayout.TextField(instanceSearchString, GUILayout.Width(200));
			if (newInstSearch != instanceSearchString)
			{
				instanceSearchString = newInstSearch;
				LoadObjectsOfType(scriptableObjectTypes[selectedTypeIndex]);
			}

			EditorGUILayout.EndHorizontal();
			EditorGUILayout.BeginHorizontal();

			EditorGUILayout.LabelField("Amount", GUILayout.Width(50));
			createCount = EditorGUILayout.IntField(createCount, GUILayout.Width(40));
			createCount = Mathf.Max(1, createCount);
			if (GUILayout.Button(EditorGUIUtility.IconContent("d_Toolbar Plus"), GUILayout.Width(30),
				    GUILayout.Height(18)))
			{
				CreateNewInstance(createCount);
			}

			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();
			if (currentTypeObjects.Any() && typeNames.Any())
			{
				DrawPropertiesGrid();
			}

			EditorGUILayout.EndVertical();
			EditorGUILayout.EndScrollView();
		}

		private bool Fold(string key)
		{
			var val = EditorGUILayout.Foldout(GetExpandedRegion(key), key);
			regions[key] = val;
			return val;
		}

		private void ApplySorting()
		{
			if (sortColumnIndex < 0) return;
			if (sortColumnIndex == 1)
			{
				currentTypeObjects = (sortAscending
					? currentTypeObjects.OrderBy(o => o.name)
					: currentTypeObjects.OrderByDescending(o => o.name)).ToList();
			}
			else if (sortColumnIndex >= 2)
			{
				var first = new SerializedObject(currentTypeObjects[0]);
				var iter = first.GetIterator();
				var paths = new List<string>();
				if (iter.NextVisible(true))
					do
					{
						paths.Add(iter.propertyPath);
					} while (iter.NextVisible(false));

				if (sortColumnIndex == 2)
				{
					currentTypeObjects = (sortAscending
						? currentTypeObjects.OrderBy(o => o.name)
						: currentTypeObjects.OrderByDescending(o => o.name)).ToList();
				}
				else if (sortColumnIndex >= 3)
				{
					int propIndex = sortColumnIndex - 3;
					if (propIndex < paths.Count)
					{
						string path = paths[propIndex];
						var so = new SerializedObject(currentTypeObjects[0]);
						var prop = so.FindProperty(path);
						if (prop.propertyType == SerializedPropertyType.Color)
						{
							currentTypeObjects = (sortAscending
								? currentTypeObjects.OrderBy(o =>
								{
									var c = new SerializedObject(o).FindProperty(path).colorValue;
									return c.r + c.g + c.b + c.a;
								})
								: currentTypeObjects.OrderByDescending(o =>
								{
									var c = new SerializedObject(o).FindProperty(path).colorValue;
									return c.r + c.g + c.b + c.a;
								})).ToList();
						}
						else
						{
							currentTypeObjects = (sortAscending
								? currentTypeObjects.OrderBy(o => GetPropertyValue(o, path))
								: currentTypeObjects.OrderByDescending(o => GetPropertyValue(o, path))).ToList();
						}
					}
				}
			}
		}

		private IComparable GetPropertyValue(ScriptableObject o, string path)
		{
			var so = new SerializedObject(o);
			var prop = so.FindProperty(path);
			switch (prop.propertyType)
			{
				case SerializedPropertyType.Integer: return prop.intValue;
				case SerializedPropertyType.Boolean: return prop.boolValue;
				case SerializedPropertyType.Float: return prop.floatValue;
				case SerializedPropertyType.String: return prop.stringValue;
				case SerializedPropertyType.ObjectReference:
					return prop.objectReferenceValue?.name ?? string.Empty;
				default:
					return string.Empty;
			}
		}

		private void SaveColumnWidthsForCurrentType()
		{
			if (selectedTypeIndex < 0 || selectedTypeIndex >= scriptableObjectTypes.Count) return;
			string key = GetPrefsKeyForType(scriptableObjectTypes[selectedTypeIndex]);
			string data = string.Join(",", columnWidths.Select(w => w.ToString()));
			EditorPrefs.SetString(key, data);
		}

		private void LoadColumnWidthsForCurrentType(int expectedCount)
		{
			columnWidths.Clear();
			if (selectedTypeIndex < 0 || selectedTypeIndex >= scriptableObjectTypes.Count)
			{
				InitializeDefaultWidths(expectedCount);
				return;
			}

			string key = GetPrefsKeyForType(scriptableObjectTypes[selectedTypeIndex]);
			if (EditorPrefs.HasKey(key))
			{
				string data = EditorPrefs.GetString(key);
				var parts = data.Split(',');
				foreach (var p in parts)
				{
					if (float.TryParse(p, out var f)) columnWidths.Add(f);
				}
			}

			if (columnWidths.Count != expectedCount)
			{
				InitializeDefaultWidths(expectedCount);
			}
		}

		private void InitializeDefaultWidths(int count)
		{
			columnWidths.Clear();
			columnWidths.Add(40);
			columnWidths.Add(45);
			columnWidths.Add(150);
			for (int i = 3; i < count; i++) columnWidths.Add(100);
		}

		private string GetPrefsKeyForType(Type t)
		{
			return $"SOEditor_ColumnWidths_{t.AssemblyQualifiedName}";
		}

		private void DrawHeaderCell(string label, float width, int columnIndex)
		{
			var content = new GUIContent(label + (sortColumnIndex == columnIndex ? (sortAscending ? " ▲" : " ▼") : ""));
			Rect cellRect = GUILayoutUtility.GetRect(content, EditorStyles.boldLabel, GUILayout.Width(width));
			GUI.Label(cellRect, content, EditorStyles.boldLabel);
			Rect handleRect = new Rect(cellRect.xMax - 4, cellRect.y, 8, cellRect.height);
			EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeHorizontal);
			var e = Event.current;
			if (e.type == EventType.MouseDown)
			{
				if (handleRect.Contains(e.mousePosition))
				{
					draggingColumn = columnIndex;
					dragStartMouseX = e.mousePosition.x;
					dragStartWidth = columnWidths[columnIndex];
					e.Use();
				}
				else if (cellRect.Contains(e.mousePosition))
				{
					if (sortColumnIndex == columnIndex) sortAscending = !sortAscending;
					else
					{
						sortColumnIndex = columnIndex;
						sortAscending = true;
					}

					LoadObjectsOfType(scriptableObjectTypes[selectedTypeIndex]);
					e.Use();
				}
			}
			else if (e.type == EventType.MouseDrag && draggingColumn == columnIndex)
			{
				columnWidths[columnIndex] = Mathf.Max(20, dragStartWidth + (e.mousePosition.x - dragStartMouseX));
				Repaint();
				e.Use();
			}
			else if (e.type == EventType.MouseUp && draggingColumn == columnIndex)
			{
				draggingColumn = -1;
				SaveColumnWidthsForCurrentType();
				e.Use();
			}
		}

		private void CreateNewInstance(int count)
		{
			if (selectedTypeIndex < 0 || selectedTypeIndex >= scriptableObjectTypes.Count) return;
			Type type = scriptableObjectTypes[selectedTypeIndex];

			string defaultFolder = assetsFolderPath;
			if (currentTypeObjects.Count > 0)
			{
				string firstPath = AssetDatabase.GetAssetPath(currentTypeObjects[0]);
				string dir = Path.GetDirectoryName(firstPath);
				if (!string.IsNullOrEmpty(dir))
					defaultFolder = dir;
			}

			string basePath = EditorUtility.SaveFilePanelInProject(
				$"Create New {type.Name}",
				type.Name + ".asset",
				"asset",
				"Specify location to save new asset",
				defaultFolder);
			if (string.IsNullOrEmpty(basePath)) return;

			for (int i = 0; i < count; i++)
			{
				string uniquePath = (i == 0)
					? basePath
					: AssetDatabase.GenerateUniqueAssetPath(basePath);

				var instance = ScriptableObject.CreateInstance(type);
				AssetDatabase.CreateAsset(instance, uniquePath);
			}

			AssetDatabase.SaveAssets();
			LoadObjectsOfType(type);
		}

		   private void DrawPropertiesGrid()
        {
            if (!currentTypeObjects.Any()) return;

            var firstSO = new SerializedObject(currentTypeObjects[0]);
            var iter = firstSO.GetIterator();
            var propertyPaths = new List<string>();
            if (iter.NextVisible(true))
                do { propertyPaths.Add(iter.propertyPath); } while (iter.NextVisible(false));

            int totalCols = 3 + propertyPaths.Count;
            if (columnWidths.Count != totalCols)
            {
                columnWidths.Clear();
                columnWidths.Add(35);
                columnWidths.Add(35);
                columnWidths.Add(150);
                foreach (var path in propertyPaths)
                    columnWidths.Add(Mathf.Max(100, path.Length * 10));
            }

            EditorGUILayout.BeginHorizontal("box");
            DrawHeaderCell("Actions", columnWidths[0], 0);
            DrawHeaderCell("Delete", columnWidths[1], 1);
            DrawHeaderCell("Instance Name", columnWidths[2], 2);
            for (int i = 0; i < propertyPaths.Count; i++)
                DrawHeaderCell(propertyPaths[i], columnWidths[i + 3], i + 3);
            EditorGUILayout.EndHorizontal();

            var toAdd = new List<ScriptableObject>();
            var toRemove = new List<ScriptableObject>();

            foreach (var obj in currentTypeObjects)
            {
                var so = new SerializedObject(obj);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(EditorGUIUtility.IconContent("d_Toolbar Plus"), GUILayout.Width(columnWidths[0]), GUILayout.Height(18)))
                    toAdd.Add(obj);
                if (GUILayout.Button(EditorGUIUtility.IconContent("d_TreeEditor.Trash"), GUILayout.Width(columnWidths[1]), GUILayout.Height(18)))
                    toRemove.Add(obj);
                EditorGUILayout.LabelField(obj.name, EditorStyles.textField, GUILayout.Width(columnWidths[2]));

                for (int i = 0; i < propertyPaths.Count; i++)
                {
                    var path = propertyPaths[i];
                    var prop = so.FindProperty(path);
                    if (prop == null) continue;

                    FieldInfo field = obj.GetType()
                        .GetField(prop.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var rangeAttr = field?.GetCustomAttribute<RangeAttribute>(true);
                    var minAttr = field?.GetCustomAttribute<MinAttribute>(true);
                    var textAreaAttr = field?.GetCustomAttribute<TextAreaAttribute>(true);
                    var colorUsageAttr = field?.GetCustomAttribute<ColorUsageAttribute>(true);

                    GUILayoutOption widthOpt = GUILayout.Width(columnWidths[i + 3]);

                    switch (prop.propertyType)
                    {
                        case SerializedPropertyType.Float when rangeAttr != null:
                            prop.floatValue = EditorGUILayout.Slider(prop.floatValue, rangeAttr.min, rangeAttr.max, widthOpt);
                            break;
                        case SerializedPropertyType.Integer when rangeAttr != null:
                            prop.intValue = EditorGUILayout.IntSlider(prop.intValue, (int)rangeAttr.min, (int)rangeAttr.max, widthOpt);
                            break;
                        case SerializedPropertyType.Float when minAttr != null:
                            prop.floatValue = Mathf.Max(minAttr.min, EditorGUILayout.FloatField(prop.floatValue, widthOpt));
                            break;
                        case SerializedPropertyType.Integer when minAttr != null:
                            prop.intValue = Mathf.Max((int)minAttr.min, EditorGUILayout.IntField(prop.intValue, widthOpt));
                            break;
                        case SerializedPropertyType.String when textAreaAttr != null:
                            string str = prop.stringValue;
                            str = EditorGUILayout.TextArea(str, GUILayout.Height(textAreaAttr.minLines * 14), GUILayout.Width(columnWidths[i + 3]));
                            prop.stringValue = str;
                            break;
                        case SerializedPropertyType.Color:
                            bool showAlpha = colorUsageAttr?.showAlpha ?? true;
                            bool hdr = colorUsageAttr?.hdr ?? false;
                            prop.colorValue = EditorGUILayout.ColorField(GUIContent.none, prop.colorValue, true, showAlpha, hdr, widthOpt);
                            break;
                        default:
                            EditorGUILayout.PropertyField(prop, GUIContent.none, widthOpt);
                            break;
                    }
                }

                EditorGUILayout.EndHorizontal();
                so.ApplyModifiedProperties();
            }

            foreach (var add in toAdd)
            {
                var assetPath = AssetDatabase.GetAssetPath(add);
                if (TryGetUniqueAssetPath(assetPath, out var newAssetPath))
                {
                    var newObj = Instantiate(add);
                    AssetDatabase.CreateAsset(newObj, newAssetPath);
                    AssetDatabase.SaveAssets();
                }
            }

            foreach (var obj in toRemove)
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.SaveAssets();
            }
        }

		private bool TryGetUniqueAssetPath(string originalPath, out string uniquePath)
		{
			uniquePath = null;
			try
			{
				var directory = Path.GetDirectoryName(originalPath);
				var extension = Path.GetExtension(originalPath);
				var name = Path.GetFileNameWithoutExtension(originalPath);
				if (directory == null) return false;

				int pos = name.Length - 1;
				while (pos >= 0 && char.IsDigit(name[pos])) pos--;
				bool hasNumber = pos < name.Length - 1;
				string baseName = hasNumber ? name.Substring(0, pos + 1) : name;
				int index = 0;
				if (hasNumber && !int.TryParse(name.Substring(pos + 1), out index)) index = 0;

				do
				{
					index++;
					var candidate = baseName + (hasNumber ? index.ToString() : "_Copy" + index) + extension;
					uniquePath = Path.Combine(directory, candidate);
				} while (File.Exists(uniquePath));

				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}